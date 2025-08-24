import { Component, Inject, OnInit } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { lastValueFrom } from 'rxjs';

@Component({
  selector: 'app-agents',
  templateUrl: './agents.component.html',
})
export class AgentsComponent implements OnInit {
  public registeredAgents: RegisteredAgent[] = [];
  public isLoading = true;
  public error: string | null = null;

  constructor(
    private http: HttpClient,
    @Inject('BASE_URL') private baseUrl: string
  ) {}

  async ngOnInit() {
    await this.loadAgents();
  }

  async loadAgents() {
    try {
      this.isLoading = true;
      this.error = null;

      const token = localStorage.getItem('auth_token'); // Fixed: use correct token key
      if (!token) {
        this.error = 'Authentication required. Please log in.';
        return;
      }

      const headers = new HttpHeaders().set('Authorization', `Bearer ${token}`);

      this.registeredAgents = await lastValueFrom(
        this.http.get<RegisteredAgent[]>(
          this.baseUrl + 'api/v1/admin/agents',
          { headers }
        )
      );
    } catch (error: any) {
      console.error('Error loading agents:', error);
      if (error.status === 401) {
        this.error = 'Authentication failed. Please log in again.';
      } else {
        this.error = 'Failed to load agents. Please try again.';
      }
    } finally {
      this.isLoading = false;
    }
  }

  async authorizeAgent(agentId: string) {
    try {
      const token = localStorage.getItem('auth_token');
      const headers = new HttpHeaders().set('Authorization', `Bearer ${token}`);

      await lastValueFrom(
        this.http.post(
          `${this.baseUrl}api/v1/admin/agents/${agentId}/authorize`,
          {},
          { headers }
        )
      );

      // Reload agents to reflect changes
      await this.loadAgents();
    } catch (error) {
      console.error('Error authorizing agent:', error);
      this.error = 'Failed to authorize agent. Please try again.';
    }
  }

  async deauthorizeAgent(agentId: string) {
    try {
      const token = localStorage.getItem('auth_token');
      const headers = new HttpHeaders().set('Authorization', `Bearer ${token}`);

      await lastValueFrom(
        this.http.post(
          `${this.baseUrl}api/v1/admin/agents/${agentId}/deauthorize`,
          {},
          { headers }
        )
      );

      // Reload agents to reflect changes
      await this.loadAgents();
    } catch (error) {
      console.error('Error deauthorizing agent:', error);
      this.error = 'Failed to deauthorize agent. Please try again.';
    }
  }

  async deleteAgent(agentId: string) {
    if (!confirm('Are you sure you want to delete this agent? This action cannot be undone.')) {
      return;
    }

    try {
      const token = localStorage.getItem('auth_token');
      const headers = new HttpHeaders().set('Authorization', `Bearer ${token}`);

      await lastValueFrom(
        this.http.delete(
          `${this.baseUrl}api/v1/admin/agents/${agentId}`,
          { headers }
        )
      );

      // Reload agents to reflect changes
      await this.loadAgents();
    } catch (error) {
      console.error('Error deleting agent:', error);
      this.error = 'Failed to delete agent. Please try again.';
    }
  }

  get pendingAgents() {
    return this.registeredAgents.filter(agent => !agent.isAuthorized);
  }

  get authorizedAgents() {
    return this.registeredAgents.filter(agent => agent.isAuthorized);
  }

  getTimeSinceLastSeen(lastSeenAt: string): string {
    const lastSeen = new Date(lastSeenAt);
    const now = new Date();
    const diffInMinutes = Math.floor((now.getTime() - lastSeen.getTime()) / (1000 * 60));

    if (diffInMinutes < 1) return 'Just now';
    if (diffInMinutes < 60) return `${diffInMinutes} minutes ago`;
    if (diffInMinutes < 1440) return `${Math.floor(diffInMinutes / 60)} hours ago`;
    return `${Math.floor(diffInMinutes / 1440)} days ago`;
  }
}

interface RegisteredAgent {
  agentId: string;
  serviceName: string;
  operatingSystem: string;
  isAuthorized: boolean;
  registeredAt: string;
  lastSeenAt: string;
  authorizedAt?: string;
  authorizedBy?: string;
}
