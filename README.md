# JMW.Agent

A comprehensive system monitoring and management solution built with .NET 9, featuring a client-server architecture for monitoring distributed systems.

## 🚀 Overview

JMW.Agent is a distributed monitoring system that consists of:

- **Agent Client**: A lightweight service that collects system information and reports to the central server
- **Agent Server**: A web-based dashboard with REST API for managing and monitoring connected agents
- **Common Library**: Shared models and utilities for data collection and serialization

## 🏗️ Architecture

```
┌─────────────────┐    HTTPS    ┌─────────────────┐
│   Agent Client  │ ──────────► │  Agent Server   │
│   (Monitoring)  │             │   (Dashboard)   │
└─────────────────┘             └─────────────────┘
        │                               │
        ▼                               ▼
┌─────────────────┐             ┌─────────────────┐
│ System Metrics  │             │   SQLite DB     │
│   Collection    │             │   Web UI        │
└─────────────────┘             └─────────────────┘
```

## 📋 Features

### Agent Client
- 🔍 **System Information Collection**: CPU, memory, disk, network interfaces
- 🌐 **Network Monitoring**: IP addresses, interface statistics, neighbors
- 💻 **OS Detection**: Windows (WMI) and Linux support
- 🔐 **Secure Registration**: Automatic agent registration with authorization
- ⚡ **Real-time Reporting**: Continuous system state updates
- 🖥️ **Service Integration**: Systemd support for Linux deployment

### Agent Server
- 📊 **Web Dashboard**: Angular-based frontend for agent management
- 🔌 **REST API**: Complete API for agent registration and data retrieval
- 🗄️ **Data Persistence**: SQLite database for agent data storage
- 🔐 **Authentication**: JWT-based security with Identity framework
- 📈 **Monitoring**: OpenTelemetry integration for observability
- 📱 **Responsive UI**: Modern web interface for system monitoring

## 🛠️ Technology Stack

- **.NET 9**: Core framework
- **ASP.NET Core**: Web framework and API
- **Entity Framework Core**: Data access with SQLite
- **Angular**: Frontend framework
- **OpenTelemetry**: Observability and monitoring
- **System.Text.Json**: High-performance JSON serialization
- **Microsoft Extensions**: Dependency injection, logging, configuration

## 🚀 Getting Started

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Node.js](https://nodejs.org/) (for the web UI)
- Linux/Windows operating system

### Building the Solution

```bash
# Clone the repository
git clone <repository-url>
cd JMW.Agent

# Restore dependencies
dotnet restore

# Build the entire solution
dotnet build
```

### Running the Server

```bash
cd src/Server/JMW.Agent.Server
dotnet run
```

The server will start on `https://localhost:5001` by default.

### Running an Agent Client

```bash
cd src/Agent
dotnet run
```

### Configuration

#### Agent Client Configuration (`appsettings.json`)

```json
{
  "AgentOptions": {
    "ServerIp": "localhost",
    "ServerPort": 5001,
    "ServiceName": "MyAgent",
    "AgentIdFilePath": "agent.id"
  }
}
```

#### Server Configuration (`appsettings.json`)

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=jmw.agent.sqlite"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  }
}
```

## 📁 Project Structure

```
JMW.Agent/
├── src/
│   ├── Agent/                          # Agent Client
│   │   ├── Services/                   # Agent services
│   │   ├── AgentOptions.cs            # Configuration model
│   │   ├── ReportingService.cs        # Main reporting logic
│   │   └── Program.cs                 # Entry point
│   ├── JMW.Agent.Common/              # Shared library
│   │   ├── Models/                    # Data models
│   │   ├── Serialization/             # JSON converters
│   │   ├── Windows/                   # Windows-specific WMI
│   │   └── Linux/                     # Linux-specific services
│   └── Server/
│       └── JMW.Agent.Server/          # Server application
│           ├── ClientApp/             # Angular frontend
│           ├── Data/                  # Entity Framework
│           ├── Endpoints/             # API endpoints
│           └── Services/              # Server services
└── JMW.Agent.sln                     # Solution file
```

## 🔧 Development

### Prerequisites for Development

- [Visual Studio 2022](https://visualstudio.microsoft.com/) or [JetBrains Rider](https://www.jetbrains.com/rider/)
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Node.js 18+](https://nodejs.org/)

### Development Workflow

1. **Backend Development**: Use Visual Studio or Rider to work with the .NET projects
2. **Frontend Development**: The Angular app is in `src/Server/JMW.Agent.Server/ClientApp/`
3. **Testing**: Run unit tests with `dotnet test`
4. **Database**: SQLite database is created automatically on first run

### Adding New System Metrics

1. Add new model classes in `JMW.Agent.Common/Models/`
2. Implement collection logic in the appropriate OS-specific service
3. Update the reporting service to include new metrics
4. Add corresponding UI components in the Angular frontend

## 🐧 Linux Deployment

The agent supports systemd for Linux deployment:

```bash
# Build and publish
dotnet publish -c Release -o /opt/jmw-agent

# Create systemd service file
sudo nano /etc/systemd/system/jmw-agent.service

# Enable and start service
sudo systemctl enable jmw-agent
sudo systemctl start jmw-agent
```

## 🔐 Security

- Agent registration requires server authorization
- HTTPS communication between agent and server
- JWT-based authentication for web interface
- Configurable agent identification and validation

## 📊 Monitoring & Observability

- OpenTelemetry integration for metrics and tracing
- Prometheus metrics endpoint
- Structured logging with Microsoft.Extensions.Logging
- Real-time system performance monitoring

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## 📝 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🆘 Support

For support and questions:

- Create an issue in the GitHub repository
- Check the documentation in the `/docs` folder
- Review the API documentation at `/swagger` when the server is running

## 🗺️ Roadmap

- [ ] Docker containerization
- [ ] Additional database providers (PostgreSQL, SQL Server)
- [ ] Advanced alerting and notifications
- [ ] Performance benchmarking tools
- [ ] Multi-tenant support
- [ ] Enhanced security features
