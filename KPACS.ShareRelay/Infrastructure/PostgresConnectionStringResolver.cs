using System.Text;

namespace KPACS.ShareRelay.Infrastructure;

public static class PostgresConnectionStringResolver
{
    public static string Resolve(IConfiguration configuration)
    {
        string? direct = configuration.GetConnectionString("RelayDb");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        string? railwayUrl = configuration["DATABASE_URL"];
        if (!string.IsNullOrWhiteSpace(railwayUrl))
        {
            return ConvertRailwayUrl(railwayUrl);
        }

        throw new InvalidOperationException("No PostgreSQL connection string was configured. Set ConnectionStrings:RelayDb or DATABASE_URL.");
    }

    private static string ConvertRailwayUrl(string databaseUrl)
    {
        if (!Uri.TryCreate(databaseUrl, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException("DATABASE_URL is not a valid PostgreSQL URI.");
        }

        string userInfo = Uri.UnescapeDataString(uri.UserInfo);
        string[] userParts = userInfo.Split(':', 2, StringSplitOptions.None);
        string username = userParts.ElementAtOrDefault(0) ?? string.Empty;
        string password = userParts.ElementAtOrDefault(1) ?? string.Empty;
        string database = uri.AbsolutePath.Trim('/');

        var builder = new StringBuilder();
        builder.Append("Host=").Append(uri.Host).Append(';');
        builder.Append("Port=").Append(uri.Port).Append(';');
        builder.Append("Database=").Append(database).Append(';');
        builder.Append("Username=").Append(username).Append(';');
        builder.Append("Password=").Append(password).Append(';');
        builder.Append("Ssl Mode=Require;Trust Server Certificate=true");
        return builder.ToString();
    }
}
