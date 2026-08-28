using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OfficeCal.Infrastructure;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OfficeCalDbContext>
{
    public const string DefaultConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=OfficeCal;Integrated Security=true;" +
        "MultipleActiveResultSets=true;TrustServerCertificate=true";

    public OfficeCalDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OfficeCalDbContext>()
            .UseSqlServer(DefaultConnectionString)
            .Options;
        return new OfficeCalDbContext(options);
    }
}
