using System.Xml.Linq;
using Dapper;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;
namespace DragonLord.Api;

// Share the ASP.NET Data Protection key ring between short-lived cloud containers.
public sealed class PostgresXmlRepository(NpgsqlDataSource source) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = source.OpenConnection();
        return connection.Query<string>("SELECT xml FROM data_protection_keys ORDER BY id").Select(XElement.Parse).ToArray();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var connection = source.OpenConnection();
        connection.Execute("INSERT INTO data_protection_keys(friendly_name,xml) VALUES(@friendlyName,@xml)", new { friendlyName, xml=element.ToString(SaveOptions.DisableFormatting) });
    }
}
