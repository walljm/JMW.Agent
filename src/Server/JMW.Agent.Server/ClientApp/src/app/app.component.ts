import { Component, OnInit } from '@angular/core';
import { Router, NavigationEnd } from '@angular/router';
import { filter } from 'rxjs/operators';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html'
})
export class AppComponent implements OnInit {
  title = 'app';

  public showNavMenu = false;

  constructor(private router: Router) {}

  ngOnInit() {
    // Check if we're on an authentication route
    this.router.events.pipe(
      filter(event => event instanceof NavigationEnd)
    ).subscribe((event) => {
      if (event instanceof NavigationEnd) {
        this.showNavMenu = !event.url.startsWith('/authentication');
      }
    });

    // Check initial route
    this.showNavMenu = !this.router.url.startsWith('/authentication');
  }
}
