import { Component, OnInit } from '@angular/core';
import { AuthorizeService } from '../authorize.service';
import { ActivatedRoute, Router } from '@angular/router';
import { LogoutActions, ApplicationPaths, QueryParameterNames } from '../api-authorization.constants';

@Component({
  selector: 'app-logout',
  templateUrl: './logout.component.html',
  styleUrls: ['./logout.component.css']
})
export class LogoutComponent implements OnInit {
  public message: string | null = null;
  public isLoading = false;

  constructor(
    private authorizeService: AuthorizeService,
    private activatedRoute: ActivatedRoute,
    private router: Router) { }

  async ngOnInit() {
    const action = this.activatedRoute.snapshot.url[1];
    switch (action?.path) {
      case LogoutActions.Logout:
        await this.logout();
        break;
      case LogoutActions.LoggedOut:
        this.message = 'You successfully logged out!';
        break;
      default:
        // Default logout action
        await this.logout();
        break;
    }
  }

  private async logout(): Promise<void> {
    this.isLoading = true;

    try {
      await this.authorizeService.logout().toPromise();
      this.message = 'You have been successfully logged out.';

      const returnUrl = this.getReturnUrl();
      setTimeout(() => {
        this.router.navigateByUrl(returnUrl);
      }, 2000); // Show message for 2 seconds before redirecting

    } catch (error) {
      console.error('Logout error:', error);
      this.message = 'Logout completed locally.';

      const returnUrl = this.getReturnUrl();
      setTimeout(() => {
        this.router.navigateByUrl(returnUrl);
      }, 2000);
    } finally {
      this.isLoading = false;
    }
  }

  private getReturnUrl(): string {
    return this.activatedRoute.snapshot.queryParamMap.get(QueryParameterNames.ReturnUrl) ||
      ApplicationPaths.DefaultLoginRedirectPath;
  }
}
