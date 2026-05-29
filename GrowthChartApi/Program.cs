using System.Collections.Concurrent;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddCors(o => o.AddPolicy("dev",
    b => b.WithOrigins(allowedOrigins)
          .AllowAnyHeader()
          .AllowAnyMethod()));

var app = builder.Build();
app.UseCors("dev");

// ─── JSON FILE PERSISTENCE ─────────────────────────────────────────────────────
var storageFile = Path.Combine(AppContext.BaseDirectory, "patients.json");
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

void SaveStore(ConcurrentDictionary<string, StoredPatient> s)
{
    try { File.WriteAllText(storageFile, JsonSerializer.Serialize(s, jsonOptions)); }
    catch (Exception ex) { Console.WriteLine($"Warning: could not save patients.json: {ex.Message}"); }
}

var store = new ConcurrentDictionary<string, StoredPatient>();
if (File.Exists(storageFile))
{
    try
    {
        var loaded = JsonSerializer.Deserialize<ConcurrentDictionary<string, StoredPatient>>(
            File.ReadAllText(storageFile), jsonOptions);
        if (loaded != null)
            foreach (var kvp in loaded) store[kvp.Key] = kvp.Value;
        Console.WriteLine($"Loaded {store.Count} patient(s) from patients.json");
    }
    catch (Exception ex) { Console.WriteLine($"Warning: could not load patients.json: {ex.Message}"); }
}

// ─── HELPER: age in months ────────────────────────────────────────────────────
static double AgeInMonths(string dob, string observationDate)
{
    if (!DateTime.TryParse(dob, out var birth) ||
        !DateTime.TryParse(observationDate, out var obs))
        return 0;
    return (obs.Year - birth.Year) * 12 + (obs.Month - birth.Month)
           + (obs.Day - birth.Day) / 30.0;
}

// ─── HELPER: build chart-ready processed data ─────────────────────────────────
static ProcessedPatientData BuildProcessed(StoredPatient p)
{
    var weightData = p.Observations
        .Where(o => o.Weight.HasValue)
        .Select(o => new VitalReading
        {
            Agemos = AgeInMonths(p.Dob, o.Date),
            Value  = o.Weight!.Value
        }).ToList();

    var lengthData = p.Observations
        .Where(o => o.Height.HasValue)
        .Select(o => new VitalReading
        {
            Agemos = AgeInMonths(p.Dob, o.Date),
            Value  = o.Height!.Value
        }).ToList();

    var headCData = p.Observations
        .Where(o => o.HeadCircumference.HasValue)
        .Select(o => new VitalReading
        {
            Agemos = AgeInMonths(p.Dob, o.Date),
            Value  = o.HeadCircumference!.Value
        }).ToList();

    var bmiData = p.Observations
        .Where(o => o.Weight.HasValue && o.Height.HasValue && o.Height > 0)
        .Select(o => new VitalReading
        {
            Agemos = AgeInMonths(p.Dob, o.Date),
            Value  = Math.Round(o.Weight!.Value / Math.Pow(o.Height!.Value / 100.0, 2), 1)
        }).ToList();

    return new ProcessedPatientData
    {
        Demographics = new Dictionary<string, object>
        {
            { "name",     p.Name },
            { "birthday", p.Dob },
            { "gender",   p.Gender }
        },
        Vitals = new Dictionary<string, List<VitalReading>>
        {
            { "weightData", weightData },
            { "lengthData", lengthData },
            { "headCData",  headCData  },
            { "BMIData",    bmiData    }
        },
        BoneAge       = new(),
        FamilyHistory = new Dictionary<string, ParentData>
        {
            { "father", new() { Height = p.FatherHeight, IsBio = p.FatherHeight.HasValue } },
            { "mother", new() { Height = p.MotherHeight, IsBio = p.MotherHeight.HasValue } }
        }
    };
}

// ============ Endpoints ============

// GET /api/patients/search?uhid=xxx
app.MapGet("/api/patients/search", (string? uhid) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(uhid))
            return Results.BadRequest(new { error = "uhid parameter required." });

        var match = store.Values.FirstOrDefault(p =>
            !string.IsNullOrEmpty(p.Uhid) &&
            p.Uhid.Trim().ToLower() == uhid.Trim().ToLower());

        if (match == null)
            return Results.Json(new { found = false });

        return Results.Json(new
        {
            found        = true,
            id           = match.PatientId,
            name         = match.Name,
            dob          = match.Dob,
            gender       = match.Gender,
            fatherHeight = match.FatherHeight,
            motherHeight = match.MotherHeight
        });
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = "Internal server error", details = ex.Message },
            statusCode: 500);
    }
})
.WithName("SearchByUhid");

// POST /api/patients
app.MapPost("/api/patients", (PatientFormData form) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(form.PatientName) || string.IsNullOrWhiteSpace(form.DateOfBirth))
            return Results.BadRequest(new { error = "PatientName and DateOfBirth are required." });

        // Match priority: UHID first, then name+DOB
        StoredPatient? existing = null;

        if (!string.IsNullOrWhiteSpace(form.Uhid))
        {
            existing = store.Values.FirstOrDefault(p =>
                !string.IsNullOrEmpty(p.Uhid) &&
                p.Uhid.Trim().ToLower() == form.Uhid.Trim().ToLower());
        }

        if (existing == null)
        {
            var matchKey = $"{form.PatientName.Trim().ToLower()}|{form.DateOfBirth.Trim()}";
            existing = store.Values.FirstOrDefault(p =>
                $"{p.Name.Trim().ToLower()}|{p.Dob.Trim()}" == matchKey);
        }

        var newObs = new Observation
        {
            Date              = form.ObservationDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd"),
            Height            = form.Height,
            Weight            = form.Weight,
            HeadCircumference = form.HeadCircumference
        };

        bool isNew;
        StoredPatient patient;

        if (existing != null)
        {
            existing.Observations.Add(newObs);
            if (!string.IsNullOrWhiteSpace(form.Uhid) && string.IsNullOrEmpty(existing.Uhid))
                existing.Uhid = form.Uhid.Trim();
            if (form.FatherHeight.HasValue) existing.FatherHeight = form.FatherHeight;
            if (form.MotherHeight.HasValue) existing.MotherHeight = form.MotherHeight;
            patient = existing;
            isNew   = false;
        }
        else
        {
            var id = "patient-" + Guid.NewGuid().ToString("N");
            patient = new StoredPatient
            {
                PatientId    = id,
                Uhid         = form.Uhid?.Trim() ?? "",
                Name         = form.PatientName.Trim(),
                Dob          = form.DateOfBirth.Trim(),
                Gender       = form.Gender.Trim().ToLower(),
                FatherHeight = form.FatherHeight,
                MotherHeight = form.MotherHeight,
                Observations = new List<Observation> { newObs }
            };
            store[id] = patient;
            isNew     = true;
        }

        SaveStore(store);

        return Results.Json(new
        {
            id      = patient.PatientId,
            isNew,
            message = isNew
                ? "New patient created."
                : $"Observation added. Total readings: {patient.Observations.Count}"
        });
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = "Internal server error", details = ex.Message },
            statusCode: 500);
    }
})
.WithName("CreateOrUpdatePatient");

// GET /api/patients/{id}/data
app.MapGet("/api/patients/{id}/data", (string id) =>
{
    try
    {
        if (!store.TryGetValue(id, out var patient))
            return Results.NotFound(new { error = "Patient not found" });
        return Results.Json(BuildProcessed(patient));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = "Internal server error", details = ex.Message },
            statusCode: 500);
    }
})
.WithName("GetPatientData");

// GET /api/patients/{id}
app.MapGet("/api/patients/{id}", (string id) =>
{
    try
    {
        if (!store.TryGetValue(id, out var patient))
            return Results.NotFound(new { error = "Patient not found" });
        return Results.Json(patient);
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = "Internal server error", details = ex.Message },
            statusCode: 500);
    }
})
.WithName("GetPatient");

// GET /api/patients
app.MapGet("/api/patients", () =>
{
    try
    {
        var list = store.Values.Select(p => new
        {
            id       = p.PatientId,
            uhid     = p.Uhid,
            name     = p.Name,
            dob      = p.Dob,
            gender   = p.Gender,
            readings = p.Observations.Count
        }).ToList();
        return Results.Json(list);
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = "Internal server error", details = ex.Message },
            statusCode: 500);
    }
})
.WithName("ListPatients");

// DELETE /api/patients/{id}
app.MapDelete("/api/patients/{id}", (string id) =>
{
    try
    {
        if (!store.TryRemove(id, out _))
            return Results.NotFound(new { error = "Patient not found" });
        SaveStore(store);
        return Results.Ok(new { message = "Patient deleted." });
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = "Internal server error", details = ex.Message },
            statusCode: 500);
    }
})
.WithName("DeletePatient");

// DELETE /api/patients/{id}/observations/{index}
app.MapDelete("/api/patients/{id}/observations/{index}", (string id, int index) =>
{
    try
    {
        if (!store.TryGetValue(id, out var patient))
            return Results.NotFound(new { error = "Patient not found" });
        if (index < 0 || index >= patient.Observations.Count)
            return Results.BadRequest(new { error = "Invalid observation index." });
        patient.Observations.RemoveAt(index);
        SaveStore(store);
        return Results.Ok(new { message = $"Observation {index} removed. Remaining: {patient.Observations.Count}" });
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = "Internal server error", details = ex.Message },
            statusCode: 500);
    }
})
.WithName("DeleteObservation");

app.Run();

// ============ Models ============

public class StoredPatient
{
    public string            PatientId    { get; set; } = "";
    public string            Uhid         { get; set; } = "";
    public string            Name         { get; set; } = "";
    public string            Dob          { get; set; } = "";
    public string            Gender       { get; set; } = "";
    public double?           FatherHeight { get; set; }
    public double?           MotherHeight { get; set; }
    public List<Observation> Observations { get; set; } = new();
}

public class Observation
{
    public string  Date              { get; set; } = "";
    public double? Height            { get; set; }
    public double? Weight            { get; set; }
    public double? HeadCircumference { get; set; }
}

public class PatientFormData
{
    public string? Uhid              { get; set; }
    public string  PatientName       { get; set; } = "";
    public string  Gender            { get; set; } = "";
    public string  DateOfBirth       { get; set; } = "";
    public string? ObservationDate   { get; set; }
    public double? Height            { get; set; }
    public double? Weight            { get; set; }
    public double? HeadCircumference { get; set; }
    public double? FatherHeight      { get; set; }
    public double? MotherHeight      { get; set; }
}

public class ProcessedPatientData
{
    public Dictionary<string, object>             Demographics  { get; set; } = new();
    public Dictionary<string, List<VitalReading>> Vitals        { get; set; } = new();
    public List<object>                           BoneAge       { get; set; } = new();
    public Dictionary<string, ParentData>         FamilyHistory { get; set; } = new();
}

public class VitalReading
{
    public double Agemos { get; set; }
    public double Value  { get; set; }
}

public class ParentData
{
    public double? Height { get; set; }
    public bool    IsBio  { get; set; }
}