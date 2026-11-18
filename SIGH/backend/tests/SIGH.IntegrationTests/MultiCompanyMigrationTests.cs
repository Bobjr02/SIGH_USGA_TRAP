using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SIGH.IntegrationTests.Infrastructure;
using SIGH.Persistence.Context;
using Xunit;
using Xunit.Abstractions;

namespace SIGH.IntegrationTests;

/// <summary>
/// Migração de um banco "só USGA" (o estado da main antes da multiempresa, com dados) para a MultiCompany:
/// nada se perde, tudo fica na USGA, a Trapiche é criada e recebe a cópia dos motivos ATIVOS.
/// Usa um banco próprio no mesmo SQL Server dos testes (criado e apagado aqui).
/// </summary>
public class MultiCompanyMigrationTests
{
    private const string PreviousMigration = "20261115010906_RefreshTokenRememberMe";
    private static readonly Guid Usga = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Trap = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static readonly string[] Tables =
    [
        "Companies", "Users", "Roles", "UserRoles", "UserManagementUnits", "ManagementUnits", "JobTitles", "Employees",
        "InfractionTypes", "DisciplinaryCases", "EmployeeImportBatches", "DisciplinaryHistoryImportBatches",
        "HistoryImportStates", "AuditLogs", "Notifications"
    ];

    private readonly ITestOutputHelper _output;

    public MultiCompanyMigrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task SingleCompanyDatabaseWithData_MigratesWithoutLosingAnything_AndEverythingStaysInUsga()
    {
        var baseConnection = await SqlServerTestDatabase.GetConnectionStringAsync();
        var databaseName = $"SIGH_MigrationTest_{Guid.NewGuid():N}";
        var connectionString = new SqlConnectionStringBuilder(baseConnection) { InitialCatalog = databaseName }.ConnectionString;
        var options = new DbContextOptionsBuilder<SighDbContext>()
            .UseSqlServer(connectionString, b => b.MigrationsAssembly(typeof(SighDbContext).Assembly.FullName))
            .Options;

        try
        {
            await using (var context = new SighDbContext(options))
                await context.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await SeedSingleCompanyDataAsync(connection);

            var before = await CountAllAsync(connection);
            var activeInfractions = await ScalarAsync(connection, "SELECT COUNT(*) FROM InfractionTypes WHERE IsActive = 1 AND IsDeleted = 0");

            await using (var context = new SighDbContext(options))
                await context.Database.MigrateAsync();

            var after = await CountAllAsync(connection);

            _output.WriteLine("Tabela | antes | depois");
            foreach (var table in Tables)
                _output.WriteLine($"{table} | {before[table]} | {after[table]}");

            // Nada se perde: as contagens só mudam onde a migração acrescenta (a Trapiche e a cópia dos motivos ativos).
            foreach (var table in Tables.Except(["Companies", "InfractionTypes"]))
                after[table].Should().Be(before[table], $"nenhuma linha de {table} pode se perder");
            after["Companies"].Should().Be(before["Companies"] + 1, "a Trapiche é criada");
            after["InfractionTypes"].Should().Be(before["InfractionTypes"] + activeInfractions, "a Trapiche recebe a cópia dos motivos ativos");

            // Tudo o que existia fica na USGA.
            (await ScalarAsync(connection, $"SELECT COUNT(*) FROM UserRoles WHERE CompanyId <> '{Usga}'")).Should().Be(0);
            (await ScalarAsync(connection, $"SELECT COUNT(*) FROM Users WHERE CreatedInCompanyId IS NULL OR CreatedInCompanyId <> '{Usga}'")).Should().Be(0);
            (await ScalarAsync(connection, $"SELECT COUNT(*) FROM EmployeeImportBatches WHERE CompanyId <> '{Usga}'")).Should().Be(0);
            (await ScalarAsync(connection, $"SELECT COUNT(*) FROM DisciplinaryHistoryImportBatches WHERE CompanyId <> '{Usga}'")).Should().Be(0);
            (await ScalarAsync(connection, $"SELECT COUNT(*) FROM HistoryImportStates WHERE CompanyId <> '{Usga}'")).Should().Be(0);
            (await ScalarAsync(connection, $"SELECT COUNT(*) FROM AuditLogs WHERE CompanyId IS NULL OR CompanyId <> '{Usga}'")).Should().Be(0);
            (await ScalarAsync(connection, $"SELECT COUNT(*) FROM Employees WHERE CompanyId <> '{Usga}'")).Should().Be(0);
            (await ScalarAsync(connection, $"SELECT COUNT(*) FROM ManagementUnits WHERE CompanyId <> '{Usga}'")).Should().Be(0);
            (await ScalarAsync(connection, $"SELECT COUNT(*) FROM DisciplinaryCases WHERE CompanyId <> '{Usga}'")).Should().Be(0);
            (await ScalarAsync(connection, $"SELECT COUNT(*) FROM InfractionTypes WHERE CompanyId = '{Usga}'")).Should().Be(before["InfractionTypes"],
                "os motivos originais (ativos e inativos) continuam todos na USGA");

            // Empresas: USGA com código/prefixo; Trapiche com nome de exibição, sem nenhum nome jurídico inventado.
            (await ScalarAsync(connection, $"SELECT COUNT(*) FROM Companies WHERE Id = '{Usga}' AND Code = 'USGA' AND ManagementUnitPrefix = 'USGA' AND Name = N'Usina Serra Grande S.A.'")).Should().Be(1);
            (await ScalarAsync(connection, $"SELECT COUNT(*) FROM Companies WHERE Id = '{Trap}' AND Code = 'TRAP' AND ManagementUnitPrefix = 'TRAP' AND Name = N'Trapiche' AND LegalName IS NULL")).Should().Be(1);

            // A Trapiche recebeu exatamente os motivos ativos (mesmos códigos), e nenhuma gerência nem funcionário.
            (await ScalarAsync(connection, $@"SELECT COUNT(*) FROM InfractionTypes t WHERE t.CompanyId = '{Trap}'
                AND EXISTS (SELECT 1 FROM InfractionTypes u WHERE u.CompanyId = '{Usga}' AND u.Code = t.Code AND u.IsActive = 1)")).Should().Be(activeInfractions);
            (await ScalarAsync(connection, $"SELECT COUNT(*) FROM InfractionTypes WHERE CompanyId = '{Trap}' AND Code = 'INATIVO'")).Should().Be(0);

            // Os dados de cada linha continuam iguais (amostra: matrícula, CPF e chave do histórico).
            (await ScalarAsync(connection, "SELECT COUNT(*) FROM Employees WHERE EmployeeNumber IN ('50090001','50090002','50090003')")).Should().Be(3);
            (await ScalarAsync(connection, "SELECT COUNT(*) FROM DisciplinaryCases WHERE HistoryKey = 'HIST-KEY-1'")).Should().Be(1);

            // Os índices novos por empresa existem.
            (await ScalarAsync(connection, "SELECT COUNT(*) FROM sys.indexes WHERE name IN ('IX_Employees_CompanyId_Cpf','UX_InfractionTypes_CompanyId_Code','IX_UserRoles_UserId_RoleId_CompanyId','IX_ManagementUnits_CompanyId_Code','IX_DisciplinaryCases_CompanyId_HistoryKey','UX_Companies_Code')")).Should().Be(6);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var master = new SqlConnection(new SqlConnectionStringBuilder(baseConnection) { InitialCatalog = "master" }.ConnectionString);
            await master.OpenAsync();
            await using var drop = master.CreateCommand();
            drop.CommandText = $"IF DB_ID('{databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END";
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Dados como os de um banco da main (empresa única): usuários, perfis, gerências, funcionários, motivos, medidas, lotes...</summary>
    private static async Task SeedSingleCompanyDataAsync(SqlConnection connection)
    {
        var admin = Guid.NewGuid();
        var registrar = Guid.NewGuid();
        var adminRole = Guid.NewGuid();
        var registrarRole = Guid.NewGuid();
        var unit131 = Guid.NewGuid();
        var unit132 = Guid.NewGuid();
        var job = Guid.NewGuid();
        var employee = Guid.NewGuid();

        await InsertAsync(connection, "Companies", new() { ["Id"] = Usga, ["Name"] = "Usina Serra Grande S.A." });
        await InsertAsync(connection, "Roles", new() { ["Id"] = adminRole, ["Name"] = "Administrator" });
        await InsertAsync(connection, "Roles", new() { ["Id"] = registrarRole, ["Name"] = "HrRegistrar" });
        await InsertAsync(connection, "Users", new() { ["Id"] = admin, ["Email"] = "admin@usga.com.br", ["Cpf"] = "00000000000", ["Status"] = "Active" });
        await InsertAsync(connection, "Users", new() { ["Id"] = registrar, ["Email"] = "auxiliar@usga.com.br", ["Cpf"] = "00000000001", ["Status"] = "Active" });
        await InsertAsync(connection, "UserRoles", new() { ["UserId"] = admin, ["RoleId"] = adminRole });
        await InsertAsync(connection, "UserRoles", new() { ["UserId"] = registrar, ["RoleId"] = registrarRole });
        // Perfil retirado (exclusão lógica): também vai para a USGA.
        await InsertAsync(connection, "UserRoles", new() { ["UserId"] = registrar, ["RoleId"] = adminRole, ["IsDeleted"] = true });
        await InsertAsync(connection, "ManagementUnits", new() { ["Id"] = unit131, ["CompanyId"] = Usga, ["Code"] = "USGA131", ["Name"] = "Industrial" });
        await InsertAsync(connection, "ManagementUnits", new() { ["Id"] = unit132, ["CompanyId"] = Usga, ["Code"] = "USGA132", ["Name"] = "Manutenção" });
        await InsertAsync(connection, "UserManagementUnits", new() { ["UserId"] = registrar, ["ManagementUnitId"] = unit131 });
        await InsertAsync(connection, "JobTitles", new() { ["Id"] = job, ["CompanyId"] = Usga, ["Name"] = "Operador" });
        await InsertAsync(connection, "Employees", new() { ["Id"] = employee, ["CompanyId"] = Usga, ["EmployeeNumber"] = "50090001", ["Cpf"] = "52998224725", ["JobTitleId"] = job, ["ManagementUnitId"] = unit131, ["Status"] = 1 });
        await InsertAsync(connection, "Employees", new() { ["CompanyId"] = Usga, ["EmployeeNumber"] = "50090002", ["Cpf"] = "11144477735", ["JobTitleId"] = job, ["ManagementUnitId"] = unit132, ["Status"] = 1 });
        await InsertAsync(connection, "Employees", new() { ["CompanyId"] = Usga, ["EmployeeNumber"] = "50090003", ["Cpf"] = "39053344705", ["JobTitleId"] = job, ["ManagementUnitId"] = unit132, ["Status"] = 5 });
        await InsertAsync(connection, "InfractionTypes", new() { ["Code"] = "FALTA", ["Name"] = "Faltas injustificadas", ["IsActive"] = true, ["DefaultSeverity"] = 2 });
        await InsertAsync(connection, "InfractionTypes", new() { ["Code"] = "EPI", ["Name"] = "Falta de EPI", ["IsActive"] = true, ["DefaultSeverity"] = 3 });
        await InsertAsync(connection, "InfractionTypes", new() { ["Code"] = "INATIVO", ["Name"] = "Motivo inativo", ["IsActive"] = false, ["DefaultSeverity"] = 1 });
        await InsertAsync(connection, "DisciplinaryCases", new() { ["CompanyId"] = Usga, ["CaseNumber"] = "MED-2026-0001", ["Kind"] = 2, ["Status"] = 1, ["OpenedByUserId"] = registrar });
        await InsertAsync(connection, "DisciplinaryCases", new() { ["CompanyId"] = Usga, ["CaseNumber"] = "HIST-2025-0001", ["Kind"] = 3, ["Status"] = 1, ["HistoryKey"] = "HIST-KEY-1", ["OpenedByUserId"] = admin });
        await InsertAsync(connection, "EmployeeImportBatches", new() { ["FileName"] = "carga.xlsx", ["FileSha256"] = new string('a', 64), ["PreviewedByUserId"] = admin });
        await InsertAsync(connection, "DisciplinaryHistoryImportBatches", new() { ["FileName"] = "historico.xlsx", ["FileSha256"] = new string('b', 64), ["Status"] = "Applied", ["PreviewedByUserId"] = admin });
        await InsertAsync(connection, "HistoryImportStates", new() { ["Id"] = Guid.Parse("4a1b0000-0000-4000-8000-000000000001"), ["IsClosed"] = false });
        await InsertAsync(connection, "AuditLogs", new() { ["Action"] = "Created", ["Entity"] = "Employee", ["EntityId"] = employee.ToString(), ["UserId"] = admin });
        await InsertAsync(connection, "AuditLogs", new() { ["Action"] = "Security.LoginSucceeded", ["Entity"] = "User", ["UserId"] = registrar });
        await InsertAsync(connection, "Notifications", new() { ["CompanyId"] = Usga, ["UserId"] = registrar, ["Title"] = "Aviso", ["Message"] = "Mensagem" });
    }

    /// <summary>
    /// INSERT com os valores informados; as demais colunas obrigatórias (sem default) recebem um valor neutro pelo
    /// tipo. Assim o teste depende só das colunas que importam, não do schema completo da época.
    /// </summary>
    private static async Task InsertAsync(SqlConnection connection, string table, Dictionary<string, object> values)
    {
        var columns = new List<(string Name, string Type)>();
        await using (var meta = connection.CreateCommand())
        {
            meta.CommandText = @"SELECT c.name, t.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                                 WHERE c.object_id = OBJECT_ID(@table) AND c.is_nullable = 0 AND c.default_object_id = 0 AND c.is_identity = 0";
            meta.Parameters.AddWithValue("@table", table);
            await using var reader = await meta.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var (name, type) in columns.Where(c => !values.ContainsKey(c.Name)))
        {
            values[name] = type switch
            {
                "uniqueidentifier" => name == "Id" ? Guid.NewGuid() : Usga,
                "bit" => false,
                "int" or "bigint" or "smallint" or "tinyint" or "decimal" => 0,
                "datetimeoffset" or "datetime2" or "datetime" => DateTimeOffset.UtcNow,
                "date" => new DateTime(2026, 1, 1),
                _ => name.Contains("Email", StringComparison.Ordinal) ? $"{Guid.NewGuid():N}@usga.com.br" : name
            };
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = $"INSERT INTO [{table}] ({string.Join(", ", values.Keys.Select(k => $"[{k}]"))}) VALUES ({string.Join(", ", values.Keys.Select((_, i) => $"@p{i}"))})";
        var index = 0;
        foreach (var value in values.Values)
            insert.Parameters.AddWithValue($"@p{index++}", value);
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task<Dictionary<string, int>> CountAllAsync(SqlConnection connection)
    {
        var result = new Dictionary<string, int>();
        foreach (var table in Tables)
            result[table] = await ScalarAsync(connection, $"SELECT COUNT(*) FROM [{table}]");
        return result;
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
