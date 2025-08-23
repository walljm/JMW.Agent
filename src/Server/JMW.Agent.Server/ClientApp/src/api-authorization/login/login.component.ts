import { Component, OnInit } from '@angular/core';
import { AuthorizeService } from '../authorize.service';
import { ActivatedRoute, Router } from '@angular/router';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { LoginActions, QueryParameterNames, ApplicationPaths } from '../api-authorization.constants';

@Component({
  selector: 'app-login',
  templateUrl: './login.component.html',
  styleUrls: ['./login.component.css']
})
export class LoginComponent implements OnInit {
  public loginForm!: FormGroup;
  public message: string | null = null;
  public isLoading = false;

  constructor(
    private authorizeService: AuthorizeService,
    private activatedRoute: ActivatedRoute,
    private router: Router,
    private formBuilder: FormBuilder) { }

  ngOnInit() {
    this.loginForm = this.formBuilder.group({
      email: ['', [Validators.required, Validators.email]],
      password: ['', [Validators.required, Validators.minLength(6)]]
    });

    const action = this.activatedRoute.snapshot.url[1];
    switch (action?.path) {
      case LoginActions.LoginFailed:
        const message = this.activatedRoute.snapshot.queryParamMap.get(QueryParameterNames.Message);
        this.message = message;
        break;
      case LoginActions.Register:
        this.redirectToRegister();
        break;
      default:
        // Default login form display
        break;
    }
  }

  public async onSubmit(): Promise<void> {
    if (this.loginForm.invalid) {
      return;
    }

    this.isLoading = true;
    this.message = null;

    const { email, password } = this.loginForm.value;

    try {
      await this.authorizeService.login(email, password).toPromise();
      const returnUrl = this.getReturnUrl();
      this.router.navigateByUrl(returnUrl);
    } catch (error: any) {
      this.message = error?.error?.title || 'Login failed. Please check your credentials.';
    } finally {
      this.isLoading = false;
    }
  }

  private getReturnUrl(): string {
    return this.activatedRoute.snapshot.queryParamMap.get(QueryParameterNames.ReturnUrl) ||
      ApplicationPaths.DefaultLoginRedirectPath;
  }

  private redirectToRegister(): void {
    const returnUrl = this.getReturnUrl();
    this.router.navigate([ApplicationPaths.Register], { queryParams: { [QueryParameterNames.ReturnUrl]: returnUrl } });
  }
}
