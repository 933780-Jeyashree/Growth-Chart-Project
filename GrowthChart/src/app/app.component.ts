import { Component } from '@angular/core';
import { environment } from '../environments/environment';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.css']
})
export class AppComponent {
  // Patient fields
  uhid              = '';
  patientName       = '';
  gender            = 'other';
  fatherHeight      = '';
  motherHeight      = '';

  // Measurements
  height            = '';
  weight            = '';
  headCircumference = '';

  // Internal ISO dates
  dob               = '';
  observationDate   = '';

  // Display strings
  dobDisplay        = '';
  obsDateDisplay    = '';

  // Field errors
  dobError          = '';
  obsDateError      = '';

  // UHID lookup feedback
  uhidMessage       = '';
  uhidFound         = false;
  uhidPatientId     = '';

  // Form state
  isLoading         = false;
  errorMessage      = '';
  successMessage    = '';

  private uhidTimer: any = null;

  // ── UHID lookup ──────────────────────────────────────────────────────────────

  onUhidInput() {
    this.uhidMessage   = '';
    this.uhidFound     = false;
    this.uhidPatientId = '';

    if (!this.uhid.trim()) return;

    clearTimeout(this.uhidTimer);
    this.uhidTimer = setTimeout(() => this.lookupUhid(), 500);
  }

  lookupUhid() {
    fetch(`${environment.apiUrl}/api/patients/search?uhid=${encodeURIComponent(this.uhid.trim())}`)
      .then(r => r.json())
      .then(res => {
        if (res && res.found) {
          this.patientName   = res.name;
          this.gender        = res.gender;
          this.dobDisplay    = this.toDisplayDate(res.dob);
          this.dob           = res.dob;
          this.fatherHeight  = res.fatherHeight ? String(res.fatherHeight) : '';
          this.motherHeight  = res.motherHeight ? String(res.motherHeight) : '';
          this.uhidFound     = true;
          this.uhidMessage   = `Patient found: ${res.name}`;
          this.uhidPatientId = res.id;
        } else {
          this.uhidMessage = 'No patient found — will create new.';
          this.uhidFound   = false;
        }
      })
      .catch(() => {
        // Silently ignore — don't block the form
      });
  }

  // ── Date helpers ─────────────────────────────────────────────────────────────

  private toDisplayDate(iso: string): string {
    if (!iso || iso.length !== 10) return '';
    const [y, m, d] = iso.split('-');
    return `${d}/${m}/${y}`;
  }

  private autoSlash(value: string): string {
    const digits = value.replace(/\D/g, '').slice(0, 8);
    if (digits.length <= 2) return digits;
    if (digits.length <= 4) return `${digits.slice(0,2)}/${digits.slice(2)}`;
    return `${digits.slice(0,2)}/${digits.slice(2,4)}/${digits.slice(4)}`;
  }

  private parseDate(display: string): string {
    const parts = display.split('/');
    if (parts.length !== 3) return '';
    const [dd, mm, yyyy] = parts.map(Number);
    if (!dd || !mm || !yyyy || yyyy < 1900 || yyyy > 2100) return '';
    const d = new Date(yyyy, mm - 1, dd);
    if (d.getFullYear() !== yyyy || d.getMonth() !== mm - 1 || d.getDate() !== dd) return '';
    return `${yyyy}-${String(mm).padStart(2,'0')}-${String(dd).padStart(2,'0')}`;
  }

  onDobInput(event: Event) {
    const raw = (event.target as HTMLInputElement).value;
    this.dobDisplay = this.autoSlash(raw);
    (event.target as HTMLInputElement).value = this.dobDisplay;

    if (this.dobDisplay.length === 10) {
      const iso = this.parseDate(this.dobDisplay);
      if (iso) {
        const today = new Date().toISOString().split('T')[0];
        if (iso > today) {
          this.dobError = 'Date of birth cannot be in the future.';
          this.dob = '';
        } else {
          this.dob = iso;
          this.dobError = '';
        }
      } else {
        this.dobError = 'Invalid date. Use DD/MM/YYYY.';
        this.dob = '';
      }
    } else {
      this.dob = '';
      this.dobError = '';
    }
  }

  onObsDateInput(event: Event) {
    const raw = (event.target as HTMLInputElement).value;
    this.obsDateDisplay = this.autoSlash(raw);
    (event.target as HTMLInputElement).value = this.obsDateDisplay;

    if (this.obsDateDisplay.length === 10) {
      const iso = this.parseDate(this.obsDateDisplay);
      if (iso) {
        const today = new Date().toISOString().split('T')[0];
        if (iso > today) {
          this.obsDateError = 'Visit date cannot be in the future.';
          this.observationDate = '';
        } else {
          this.observationDate = iso;
          this.obsDateError = '';
        }
      } else {
        this.obsDateError = 'Invalid date. Use DD/MM/YYYY.';
        this.observationDate = '';
      }
    } else {
      this.observationDate = '';
      this.obsDateError = '';
    }
  }

  // ── Age display ──────────────────────────────────────────────────────────────

  getAgeDisplay(): string {
    if (!this.dob) return '';
    const birth = new Date(this.dob);
    const ref   = this.observationDate ? new Date(this.observationDate) : new Date();
    const total = (ref.getFullYear() - birth.getFullYear()) * 12
                + (ref.getMonth() - birth.getMonth());
    if (total < 0) return '';
    return `${Math.floor(total / 12)}y ${total % 12}m`;
  }

  // ── Submit ───────────────────────────────────────────────────────────────────

  onSubmit() {
    this.errorMessage   = '';
    this.successMessage = '';

    if (!this.patientName.trim()) { this.errorMessage = 'Patient name is required.'; return; }
    if (!this.dob)                { this.errorMessage = 'A valid date of birth is required (DD/MM/YYYY).'; return; }
    if (this.gender === 'other')  { this.errorMessage = 'Please select Male or Female.'; return; }
    if (this.dobError)            { this.errorMessage = this.dobError; return; }
    if (this.obsDateError)        { this.errorMessage = this.obsDateError; return; }

    this.isLoading = true;

    const payload = {
      uhid:              this.uhid.trim() || null,
      patientName:       this.patientName.trim(),
      gender:            this.gender,
      dateOfBirth:       this.dob,
      observationDate:   this.observationDate || null,
      height:            this.height            ? parseFloat(this.height)            : null,
      weight:            this.weight            ? parseFloat(this.weight)            : null,
      headCircumference: this.headCircumference ? parseFloat(this.headCircumference) : null,
      fatherHeight:      this.fatherHeight      ? parseFloat(this.fatherHeight)      : null,
      motherHeight:      this.motherHeight      ? parseFloat(this.motherHeight)      : null
    };

    fetch(`${environment.apiUrl}/api/patients`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    })
    .then(r => {
      if (!r.ok) return r.json().then(body => { throw new Error(body?.error ?? `Server error: ${r.status}`); });
      return r.json();
    })
    .then(res => {
      this.isLoading = false;
      this.successMessage = res.isNew
        ? 'New patient created. Opening chart...'
        : `Observation added. Total readings: ${res.message}. Opening chart...`;

      //  FIXED: Navigate in the same window instead of opening a new tab
      setTimeout(() => {
        window.location.href = `${environment.chartUrl}/index.html?patientId=${res.id}`;
      }, 2000);
    })
    .catch(err => {
      this.isLoading = false;
      this.errorMessage = err.message ?? `Failed to submit. Is the backend running on ${environment.apiUrl}?`;
      console.error(err);
    });
  }
}