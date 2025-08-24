using JMW.Agent.Common.Models;
using JMW.Agent.Server.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace JMW.Agent.Server.Data
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public DbSet<AgentDataPayload> AgentDataPayloads { get; set; }
        public DbSet<RegisteredAgent> RegisteredAgents { get; set; }

        public ApplicationDbContext(DbContextOptions options)
        : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure the relationship between RegisteredAgent and AgentDataPayload
            modelBuilder.Entity<AgentDataPayload>()
                .HasOne<RegisteredAgent>()
                .WithMany()
                .HasForeignKey(a => a.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}