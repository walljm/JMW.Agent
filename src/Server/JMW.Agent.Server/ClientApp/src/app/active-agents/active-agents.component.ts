import { Component, Inject, OnInit } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { lastValueFrom } from 'rxjs';

@Component({
  selector: 'app-active-agents',
  templateUrl: './active-agents.component.html',
})
export class ActiveAgentsComponent implements OnInit {
  public activeAgents: ActiveAgent[] = [];
  public selectedAgent: AgentDetail | null = null;
  public isLoading = true;
  public isLoadingDetails = false;
  public error: string | null = null;

  constructor(
    private http: HttpClient,
    @Inject('BASE_URL') private baseUrl: string
  ) {}

  async ngOnInit() {
    await this.loadActiveAgents();
  }

  async loadActiveAgents() {
    try {
      this.isLoading = true;
      this.error = null;

      const token = localStorage.getItem('auth_token'); // Fixed: use correct token key
      if (!token) {
        this.error = 'Authentication required. Please log in.';
        return;
      }

      const headers = new HttpHeaders().set('Authorization', `Bearer ${token}`);

      this.activeAgents = await lastValueFrom(
        this.http.get<ActiveAgent[]>(
          this.baseUrl + 'api/v1/admin/active-agents',
          { headers }
        )
      );
    } catch (error: any) {
      console.error('Error loading active agents:', error);
      if (error.status === 401) {
        this.error = 'Authentication failed. Please log in again.';
      } else {
        this.error = 'Failed to load active agents. Please try again.';
      }
    } finally {
      this.isLoading = false;
    }
  }

  async viewAgentDetails(agentId: string) {
    try {
      this.isLoadingDetails = true;
      this.error = null;

      const token = localStorage.getItem('auth_token'); // Fixed: use correct token key
      const headers = new HttpHeaders().set('Authorization', `Bearer ${token}`);

      this.selectedAgent = await lastValueFrom(
        this.http.get<AgentDetail>(
          `${this.baseUrl}api/v1/admin/active-agents/${agentId}/details`,
          { headers }
        )
      );
    } catch (error) {
      console.error('Error loading agent details:', error);
      this.error = 'Failed to load agent details. Please try again.';
    } finally {
      this.isLoadingDetails = false;
    }
  }

  closeDetails() {
    this.selectedAgent = null;
  }

  get agentsWithData() {
    return this.activeAgents.filter(agent => agent.hasMachineData);
  }

  getTimeSinceLastUpdate(lastUpdate: string): string {
    const lastUpdateDate = new Date(lastUpdate);
    const now = new Date();
    const diffInMinutes = Math.floor((now.getTime() - lastUpdateDate.getTime()) / (1000 * 60));

    if (diffInMinutes < 1) return 'Just now';
    if (diffInMinutes < 60) return `${diffInMinutes} minutes ago`;
    if (diffInMinutes < 1440) return `${Math.floor(diffInMinutes / 60)} hours ago`;
    return `${Math.floor(diffInMinutes / 1440)} days ago`;
  }

  getStatusBadgeClass(lastUpdate: string): string {
    const lastUpdateDate = new Date(lastUpdate);
    const now = new Date();
    const diffInMinutes = Math.floor((now.getTime() - lastUpdateDate.getTime()) / (1000 * 60));

    if (diffInMinutes < 5) return 'bg-success';
    if (diffInMinutes < 60) return 'bg-warning';
    return 'bg-danger';
  }
}

interface ActiveAgent {
  agentId: string;
  serviceName: string;
  operatingSystem: string;
  lastDataUpdate: string;
  lastSeenAt: string;
  hasMachineData: boolean;
  machineName?: string;
}

interface AgentDetail {
  agentId: string;
  serviceName: string;
  operatingSystem: string;
  registeredAt: string;
  lastSeenAt: string;
  lastDataUpdate?: string;
  machineInformation?: any;
}
