import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { BehaviorSubject, Observable, throwError } from 'rxjs';
import { map, tap, catchError } from 'rxjs/operators';

export interface LoginRequest {
  email: string;
  password: string;
}

export interface LoginResponse {
  accessToken: string;
  expiresIn: number;
  refreshToken: string;
}

export interface RegisterRequest {
  email: string;
  password: string;
}

export interface IUser {
  name?: string;
  email?: string;
}

@Injectable({
  providedIn: 'root'
})
export class AuthorizeService {
  private readonly tokenKey = 'auth_token';
  private readonly refreshTokenKey = 'refresh_token';
  private userSubject: BehaviorSubject<IUser | null> = new BehaviorSubject<IUser | null>(null);

  constructor(private http: HttpClient) {
    this.checkStoredToken();
  }

  public isAuthenticated(): Observable<boolean> {
    return this.getUser().pipe(map(u => !!u));
  }

  public getUser(): Observable<IUser | null> {
    return this.userSubject.asObservable();
  }

  public getAccessToken(): string | null {
    return localStorage.getItem(this.tokenKey);
  }

  public login(email: string, password: string): Observable<any> {
    const loginRequest: LoginRequest = { email, password };

    return this.http.post<LoginResponse>('/login', loginRequest).pipe(
      tap(response => {
        localStorage.setItem(this.tokenKey, response.accessToken);
        localStorage.setItem(this.refreshTokenKey, response.refreshToken);
        // Get user info from API instead of parsing token
        this.fetchUserInfo();
      }),
      catchError(error => {
        console.error('Login failed:', error);
        return throwError(() => error);
      })
    );
  }

  public register(email: string, password: string): Observable<any> {
    const registerRequest: RegisterRequest = { email, password };

    return this.http.post('/register', registerRequest).pipe(
      catchError(error => {
        console.error('Registration failed:', error);
        return throwError(() => error);
      })
    );
  }

  public logout(): Observable<any> {
    localStorage.removeItem(this.tokenKey);
    localStorage.removeItem(this.refreshTokenKey);
    this.userSubject.next(null);

    return this.http.post('/logout', {}).pipe(
      catchError(error => {
        console.error('Logout failed:', error);
        // Even if server logout fails, we've cleared local storage
        return throwError(() => error);
      })
    );
  }

  public refreshToken(): Observable<LoginResponse> {
    const refreshToken = localStorage.getItem(this.refreshTokenKey);
    if (!refreshToken) {
      this.logout();
      return throwError(() => new Error('No refresh token available'));
    }

    return this.http.post<LoginResponse>('/refresh', { refreshToken }).pipe(
      tap(response => {
        localStorage.setItem(this.tokenKey, response.accessToken);
        localStorage.setItem(this.refreshTokenKey, response.refreshToken);
        // Get user info from API instead of parsing token
        this.fetchUserInfo();
      }),
      catchError(error => {
        console.error('Token refresh failed:', error);
        this.logout();
        return throwError(() => error);
      })
    );
  }

  private checkStoredToken(): void {
    const token = this.getAccessToken();
    if (token) {
      // For bearer tokens, we need to fetch user info from API
      this.fetchUserInfo();
    }
  }

  private fetchUserInfo(): void {
    // Call a user info endpoint to get current user details
    this.http.get<IUser>('/manage/info').pipe(
      tap(user => {
        this.userSubject.next(user);
      }),
      catchError(error => {
        console.error('Failed to fetch user info:', error);
        // Clear invalid token
        localStorage.removeItem(this.tokenKey);
        localStorage.removeItem(this.refreshTokenKey);
        this.userSubject.next(null);
        return throwError(() => error);
      })
    ).subscribe();
  }
}
