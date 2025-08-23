using JMW.Agent.Server.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace JMW.Agent.Server.Data
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public DbSet<AgentService> AgentServices { get; set; }

        public ApplicationDbContext(DbContextOptions options)
        : base(options)
        {
        }
    }
}