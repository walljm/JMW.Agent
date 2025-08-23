import { Component, OnInit } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { Router, ActivatedRoute } from '@angular/router';
import { AuthorizeService } from '../authorize.service';
import { ApplicationPaths, QueryParameterNames } from '../api-authorization.constants';

@Component({
  selector: 'app-register',
  templateUrl: './register.component.html',
  styleUrls: ['./register.component.css']
})
export class RegisterComponent implements OnInit {
  public registerForm!: FormGroup;
  public message: string | null = null;
  public isLoading = false;

  constructor(
    private formBuilder: FormBuilder,
    private authorizeService: AuthorizeService,
    private router: Router,
    private activatedRoute: ActivatedRoute
  ) { }

  ngOnInit(): void {
    this.registerForm = this.formBuilder.group({
      email: ['', [Validators.required, Validators.email]],
      password: ['', [Validators.required, Validators.minLength(6)]],
      confirmPassword: ['', [Validators.required]]
    }, { validators: this.passwordMatchValidator });
  }

  private passwordMatchValidator(form: FormGroup) {
    const password = form.get('password');
    const confirmPassword = form.get('confirmPassword');

    if (password && confirmPassword && password.value !== confirmPassword.value) {
      confirmPassword.setErrors({ mismatch: true });
    } else {
      if (confirmPassword?.hasError('mismatch')) {
        confirmPassword.setErrors(null);
      }
    }
    return null;
  }

  public async onSubmit(): Promise<void> {
    if (this.registerForm.invalid) {
      return;
    }

    this.isLoading = true;
    this.message = null;

    const { email, password } = this.registerForm.value;

    try {
      await this.authorizeService.register(email, password).toPromise();
      this.message = 'Registration successful! Please log in with your new account.';

      // Redirect to login after successful registration
      setTimeout(() => {
        const returnUrl = this.getReturnUrl();
        this.router.navigate([ApplicationPaths.Login], {
          queryParams: { [QueryParameterNames.ReturnUrl]: returnUrl }
        });
      }, 2000);

    } catch (error: any) {
      console.error('Registration failed:', error);
      if (error?.error?.errors) {
        // Handle validation errors from server
        const errorMessages: string[] = [];
        Object.values(error.error.errors).forEach((errArray: any) => {
          if (Array.isArray(errArray)) {
            errorMessages.push(...errArray);
          } else {
            errorMessages.push(errArray);
          }
        });
        this.message = errorMessages.join(' ');
      } else {
        this.message = error?.error?.title || 'Registration failed. Please try again.';
      }
    } finally {
      this.isLoading = false;
    }
  }

  private getReturnUrl(): string {
    return this.activatedRoute.snapshot.queryParamMap.get(QueryParameterNames.ReturnUrl) ||
      ApplicationPaths.DefaultLoginRedirectPath;
  }

  public navigateToLogin(): void {
    const returnUrl = this.getReturnUrl();
    this.router.navigate([ApplicationPaths.Login], {
      queryParams: { [QueryParameterNames.ReturnUrl]: returnUrl }
    });
  }
}
