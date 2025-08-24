import { BrowserModule } from '@angular/platform-browser';
import { NgModule } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HTTP_INTERCEPTORS, provideHttpClient, withInterceptorsFromDi } from '@angular/common/http';
import { RouterModule } from '@angular/router';

import { AppComponent } from './app.component';
import { NavMenuComponent } from './nav-menu/nav-menu.component';
import { AgentsComponent } from './agents/agents.component';

import { ApiAuthorizationModule } from 'src/api-authorization/api-authorization.module';
import { AuthorizeGuard } from 'src/api-authorization/authorize.guard';
import { AuthorizeInterceptor } from 'src/api-authorization/authorize.interceptor';

@NgModule({ declarations: [
        AppComponent,
        NavMenuComponent,
        AgentsComponent,
    ],
    bootstrap: [AppComponent], imports: [BrowserModule.withServerTransition({ appId: 'ng-cli-universal' }),
        FormsModule,
        ApiAuthorizationModule,
        RouterModule.forRoot([
            { path: '', redirectTo: '/agents', pathMatch: 'full' },
            {
                path: 'agents',
                component: AgentsComponent,
                canActivate: [AuthorizeGuard],
            },
        ])], providers: [
        { provide: HTTP_INTERCEPTORS, useClass: AuthorizeInterceptor, multi: true },
        provideHttpClient(withInterceptorsFromDi()),
    ] })
export class AppModule {}
