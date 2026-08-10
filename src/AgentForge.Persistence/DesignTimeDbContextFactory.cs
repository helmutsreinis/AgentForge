using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentForge.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AgentForgeDbContext>
{
    public AgentForgeDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<AgentForgeDbContext>();
        builder.UseSqlite("Data Source=agentforge-design.db");
        return new AgentForgeDbContext(builder.Options);
    }
}
