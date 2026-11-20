using System.Net;
using System.Text.Json;
using FluentAssertions;
using SIGH.Application.Common.Authorization;
using SIGH.IntegrationTests.Infrastructure;
using SIGH.Persistence.Seeds;
using Xunit;
using S = SIGH.IntegrationTests.Scope.MixedProfilesScenario;

namespace SIGH.IntegrationTests.Scope;

/// <summary>
/// Perfis diferentes em cada empresa, no modo "Todas": cada listagem e contagem traz, de CADA empresa, só o que a
/// permissão DAQUELA empresa permite. Ex.: RH – Registro na USGA e Gestor da TRAP131 na Trapiche vê tudo da USGA e,
/// da Trapiche, só a TRAP131 — e nenhum aviso, lote ou usuário da Trapiche (o Gestor não tem essas permissões).
/// Cenário em <see cref="MixedProfilesScenario"/>; uma linha por (usuário, listagem).
/// </summary>
public class MixedProfilesTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MixedProfilesTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Resultado esperado de uma listagem: status; marcadores que TÊM de aparecer (o que a permissão de cada empresa
    /// permite) e que NÃO podem aparecer (o resto); e, para contadores, o valor exato.
    /// </summary>
    public sealed record Expectation(
        string Url,
        HttpStatusCode Status = HttpStatusCode.OK,
        Func<S, IEnumerable<string>>? Sees = null,
        Func<S, IEnumerable<string>>? Hides = null,
        Func<JsonElement, int>? Count = null,
        int ExpectedCount = 0,
        bool AllPages = false,
        ManagerUnit? OnlyUnit = null,
        bool OtherCompanyAppears = true);

    /// <summary>Empresa em que o usuário é Gestor e a gerência dele: TODO item da lista daquela empresa é dessa gerência.</summary>
    public sealed record ManagerUnit(Guid CompanyId, string UnitCode);

    private static readonly ManagerUnit OnlyTrap131 = new(DatabaseSeeder.TrapicheCompanyId, "TRAP131");
    private static readonly ManagerUnit OnlyUsga131 = new(DatabaseSeeder.DefaultCompanyId, "USGA131");

    private static IEnumerable<string> Ids(params Guid[] ids) => ids.Select(i => i.ToString());

    private static readonly string[] Trap132Employees = ["70090007", "70090008", "70090009", "70090010", "70090011", "70090012"];

    private static Expectation Forbidden(string url) => new(url, HttpStatusCode.Forbidden);

    private static int Total(JsonElement d) => d.GetProperty("total").GetInt32();
    private static int Int(JsonElement d) => d.GetInt32();

    /// <summary>
    /// RH – Registro na USGA, Gestor da TRAP131 na Trapiche. RH – Registro: medidas e funcionários (tudo), anexos,
    /// painéis, exportação e avisos. Gestor: medidas e funcionários só da gerência dele, painéis e exportação (sem
    /// avisos). Nenhum dos dois: lotes, usuários, gerências, motivos, auditoria, pendentes de aprovação.
    /// </summary>
    private static readonly Dictionary<string, Expectation> RegistrarUsgaManagerTrap = new(StringComparer.Ordinal)
    {
        ["Medidas (lista)"] = new("/api/v1/disciplinary-records?pageSize=100", OnlyUnit: OnlyTrap131,
            Sees: s => Ids(s.Usga131.Id, s.Usga132.Id, s.Trap131.Id), Hides: s => Ids(s.Trap132.Id)),
        ["Medidas (detalhe dentro do escopo, TRAP131)"] = new("/api/v1/disciplinary-records/{Trap131}"),
        ["Medidas (detalhe fora do escopo, TRAP132)"] = new("/api/v1/disciplinary-records/{Trap132}", HttpStatusCode.NotFound),
        ["Medidas (filtros: gerências e setores)"] = new("/api/v1/disciplinary-records/options",
            Sees: s => Ids(s.UsgaUnit131, s.UsgaUnit132, s.TrapUnit131, s.UsgaInfraction, s.TrapInfraction), Hides: s => Ids(s.TrapUnit132)),
        ["Anexo (download, USGA132)"] = new("/api/v1/disciplinary-cases/{Usga132}/evidences/{Usga132Evidence}/file"),
        ["Anexo (download, TRAP132)"] = new("/api/v1/disciplinary-cases/{Trap132}/evidences/{Trap132Evidence}/file", HttpStatusCode.NotFound),
        ["Relatórios (indicadores)"] = new("/api/v1/disciplinary-records/stats", Count: Total, ExpectedCount: 3),
        ["Painel (visão geral)"] = new("/api/v1/disciplinary-records/overview",
            Sees: s => Ids(s.Usga131.Id, s.Usga132.Id, s.Trap131.Id), Hides: s => Ids(s.Trap132.Id)),
        ["Exportação (CSV)"] = new("/api/v1/disciplinary-records/export",
            Sees: _ => [S.EmployeeUsga131, S.EmployeeUsga132, S.EmployeeTrap131], Hides: _ => [S.EmployeeTrap132]),
        ["Pendentes de aprovação (contador)"] = Forbidden("/api/v1/disciplinary-records/pending-count"),

        ["Funcionários (lista)"] = new("/api/v1/employees?pageSize=100", AllPages: true, OnlyUnit: OnlyTrap131,
            Sees: _ => [S.EmployeeUsga131, S.EmployeeUsga132, S.EmployeeTrap131], Hides: _ => Trap132Employees),
        ["Funcionários (detalhe, TRAP132)"] = new("/api/v1/employees/{Trap132Employee}", HttpStatusCode.NotFound),
        ["Funcionários (histórico/apoio à decisão, TRAP131)"] = new("/api/v1/employees/{Trap131Employee}/decision-support"),
        ["Funcionários (histórico/apoio à decisão, TRAP132)"] = new("/api/v1/employees/{Trap132Employee}/decision-support", HttpStatusCode.NotFound),
        ["Funcionários (filtros)"] = new("/api/v1/employees/filter-options",
            Sees: s => Ids(s.UsgaUnit131, s.UsgaUnit132, s.TrapUnit131), Hides: s => Ids(s.TrapUnit132)),

        ["Avisos (lista)"] = new("/api/v1/notifications?pageSize=100",
            Sees: _ => [S.NoticeUsgaGeneral, S.NoticeUsga132], Hides: s => [S.NoticeTrapGeneral, S.NoticeTrap131, S.NoticeTrap132, s.TrapName]),
        ["Avisos (não lidos)"] = new("/api/v1/notifications/unread",
            Sees: _ => [S.NoticeUsgaGeneral, S.NoticeUsga132], Hides: s => [S.NoticeTrapGeneral, S.NoticeTrap131, S.NoticeTrap132, s.TrapName]),
        ["Avisos (contador de não lidos)"] = new("/api/v1/notifications/unread/count", Count: Int, ExpectedCount: 2),

        ["Lotes de importação de funcionários"] = Forbidden("/api/v1/employees/import/batches?pageSize=100"),
        ["Lotes de importação do histórico"] = Forbidden("/api/v1/disciplinary-history/import/batches?pageSize=100"),
        ["Usuários (lista)"] = Forbidden("/api/v1/users?pageSize=100"),
        ["Gerências (administração)"] = Forbidden("/api/v1/management-units"),
        ["Motivos (catálogo)"] = Forbidden("/api/v1/infraction-types?pageSize=100"),
        ["Auditoria (trilha geral)"] = Forbidden("/api/v1/audit-logs?pageSize=200"),
    };

    /// <summary>O inverso: Gestor da USGA131 na USGA, RH – Registro na Trapiche.</summary>
    private static readonly Dictionary<string, Expectation> ManagerUsgaRegistrarTrap = new(StringComparer.Ordinal)
    {
        ["Medidas (lista)"] = new("/api/v1/disciplinary-records?pageSize=100", OnlyUnit: OnlyUsga131,
            Sees: s => Ids(s.Usga131.Id, s.Trap131.Id, s.Trap132.Id), Hides: s => Ids(s.Usga132.Id)),
        ["Medidas (detalhe dentro do escopo, USGA131)"] = new("/api/v1/disciplinary-records/{Usga131}"),
        ["Medidas (detalhe fora do escopo, USGA132)"] = new("/api/v1/disciplinary-records/{Usga132}", HttpStatusCode.NotFound),
        ["Medidas (filtros: gerências e setores)"] = new("/api/v1/disciplinary-records/options",
            Sees: s => Ids(s.UsgaUnit131, s.TrapUnit131, s.TrapUnit132, s.UsgaInfraction, s.TrapInfraction), Hides: s => Ids(s.UsgaUnit132)),
        ["Anexo (download, TRAP132)"] = new("/api/v1/disciplinary-cases/{Trap132}/evidences/{Trap132Evidence}/file"),
        ["Anexo (download, USGA132)"] = new("/api/v1/disciplinary-cases/{Usga132}/evidences/{Usga132Evidence}/file", HttpStatusCode.NotFound),
        ["Relatórios (indicadores)"] = new("/api/v1/disciplinary-records/stats", Count: Total, ExpectedCount: 3),
        ["Painel (visão geral)"] = new("/api/v1/disciplinary-records/overview",
            Sees: s => Ids(s.Usga131.Id, s.Trap131.Id, s.Trap132.Id), Hides: s => Ids(s.Usga132.Id)),
        ["Exportação (CSV)"] = new("/api/v1/disciplinary-records/export",
            Sees: _ => [S.EmployeeUsga131, S.EmployeeTrap131, S.EmployeeTrap132], Hides: _ => [S.EmployeeUsga132]),
        ["Pendentes de aprovação (contador)"] = Forbidden("/api/v1/disciplinary-records/pending-count"),

        ["Funcionários (lista)"] = new("/api/v1/employees?pageSize=100", AllPages: true, OnlyUnit: OnlyUsga131,
            Sees: _ => [S.EmployeeUsga131, S.EmployeeTrap131, S.EmployeeTrap132], Hides: _ => [S.EmployeeUsga132]),
        ["Funcionários (detalhe, USGA132)"] = new("/api/v1/employees/{Usga132Employee}", HttpStatusCode.NotFound),
        ["Funcionários (histórico/apoio à decisão, USGA131)"] = new("/api/v1/employees/{Usga131Employee}/decision-support"),
        ["Funcionários (histórico/apoio à decisão, USGA132)"] = new("/api/v1/employees/{Usga132Employee}/decision-support", HttpStatusCode.NotFound),
        ["Funcionários (filtros)"] = new("/api/v1/employees/filter-options",
            Sees: s => Ids(s.UsgaUnit131, s.TrapUnit131, s.TrapUnit132), Hides: s => Ids(s.UsgaUnit132)),

        ["Avisos (lista)"] = new("/api/v1/notifications?pageSize=100",
            Sees: _ => [S.NoticeTrapGeneral, S.NoticeTrap131, S.NoticeTrap132], Hides: s => [S.NoticeUsgaGeneral, S.NoticeUsga132, s.UsgaName]),
        ["Avisos (não lidos)"] = new("/api/v1/notifications/unread",
            Sees: _ => [S.NoticeTrapGeneral, S.NoticeTrap131, S.NoticeTrap132], Hides: s => [S.NoticeUsgaGeneral, S.NoticeUsga132, s.UsgaName]),
        ["Avisos (contador de não lidos)"] = new("/api/v1/notifications/unread/count", Count: Int, ExpectedCount: 3),

        ["Lotes de importação de funcionários"] = Forbidden("/api/v1/employees/import/batches?pageSize=100"),
        ["Lotes de importação do histórico"] = Forbidden("/api/v1/disciplinary-history/import/batches?pageSize=100"),
        ["Usuários (lista)"] = Forbidden("/api/v1/users?pageSize=100"),
        ["Gerências (administração)"] = Forbidden("/api/v1/management-units"),
        ["Motivos (catálogo)"] = Forbidden("/api/v1/infraction-types?pageSize=100"),
        ["Auditoria (trilha geral)"] = Forbidden("/api/v1/audit-logs?pageSize=200"),
    };

    /// <summary>
    /// Complementar: Administrador na USGA, Gestor da TRAP131 na Trapiche. O Administrador só gerencia ACESSOS: na USGA
    /// tem lotes de importação, usuários, gerências, motivos e a auditoria de acesso, e NENHUM dado de funcionário ou de
    /// medida (nem avisos, painel, relatórios, exportação ou auditoria de medidas). Da Trapiche, só o que o Gestor da
    /// TRAP131 vê — e nada de administração da Trapiche.
    /// </summary>
    private static readonly Dictionary<string, Expectation> AdminUsgaManagerTrap = new(StringComparer.Ordinal)
    {
        ["Medidas (lista)"] = new("/api/v1/disciplinary-records?pageSize=100", OnlyUnit: OnlyTrap131, OtherCompanyAppears: false,
            Sees: s => Ids(s.Trap131.Id), Hides: s => Ids(s.Usga131.Id, s.Usga132.Id, s.Trap132.Id)),
        ["Medidas (detalhe, USGA132)"] = new("/api/v1/disciplinary-records/{Usga132}", HttpStatusCode.NotFound),
        ["Medidas (filtros: gerências e setores)"] = new("/api/v1/disciplinary-records/options",
            Sees: s => Ids(s.TrapUnit131), Hides: s => Ids(s.UsgaUnit131, s.UsgaUnit132, s.TrapUnit132)),
        ["Anexo (download, USGA132)"] = new("/api/v1/disciplinary-cases/{Usga132}/evidences/{Usga132Evidence}/file", HttpStatusCode.NotFound),
        ["Relatórios (indicadores)"] = new("/api/v1/disciplinary-records/stats", Count: Total, ExpectedCount: 1),
        ["Painel (visão geral)"] = new("/api/v1/disciplinary-records/overview",
            Sees: s => Ids(s.Trap131.Id), Hides: s => Ids(s.Usga131.Id, s.Usga132.Id, s.Trap132.Id)),
        ["Exportação (CSV)"] = new("/api/v1/disciplinary-records/export",
            Sees: _ => [S.EmployeeTrap131], Hides: _ => [S.EmployeeUsga131, S.EmployeeUsga132, S.EmployeeTrap132]),
        ["Funcionários (lista)"] = new("/api/v1/employees?pageSize=100", AllPages: true, OnlyUnit: OnlyTrap131, OtherCompanyAppears: false,
            Sees: _ => [S.EmployeeTrap131], Hides: _ => [S.EmployeeUsga131, S.EmployeeUsga132, .. Trap132Employees]),
        ["Funcionários (detalhe, USGA131)"] = new("/api/v1/employees/{Usga131Employee}", HttpStatusCode.NotFound),
        ["Funcionários (histórico/apoio à decisão, USGA131)"] = new("/api/v1/employees/{Usga131Employee}/decision-support", HttpStatusCode.NotFound),
        ["Avisos (lista)"] = Forbidden("/api/v1/notifications?pageSize=100"),
        ["Avisos (contador de não lidos)"] = Forbidden("/api/v1/notifications/unread/count"),

        ["Lotes de importação de funcionários"] = new("/api/v1/employees/import/batches?pageSize=100",
            Sees: s => Ids(s.UsgaEmployeeBatch), Hides: s => [s.TrapEmployeeBatch.ToString(), s.TrapName]),
        ["Lotes de importação do histórico"] = new("/api/v1/disciplinary-history/import/batches?pageSize=100",
            Sees: s => Ids(s.UsgaHistoryBatch), Hides: s => [s.TrapHistoryBatch.ToString(), s.TrapName]),
        ["Usuários (lista)"] = new("/api/v1/users?pageSize=100",
            Sees: _ => [S.UsgaRegistrar, S.UsgaApprover, S.ManagerUsgaRegistrarTrap],
            Hides: s => [S.TrapAdmin, S.TrapRegistrar, S.TrapApprover, s.TrapName]),
        ["Usuários (detalhe, só da USGA)"] = new("/api/v1/users/{UsgaRegistrarId}"),
        ["Usuários (detalhe, só da Trapiche)"] = new("/api/v1/users/{TrapRegistrarId}", HttpStatusCode.NotFound),
        ["Usuários (perfis e gerências de quem está nas duas)"] = new("/api/v1/users/{ManagerUsgaRegistrarTrapId}/access",
            Sees: s => Ids(s.UsgaUnit131, DatabaseSeeder.DefaultCompanyId), Hides: s => [DatabaseSeeder.TrapicheCompanyId.ToString(), s.TrapName]),
        ["Usuários (situação dos administradores)"] = new("/api/v1/users/admin-status",
            Sees: _ => [CompanyDefaults.UsgaCode], Hides: s => [CompanyDefaults.TrapicheCode, s.TrapName]),
        ["Gerências (administração)"] = new("/api/v1/management-units",
            Sees: s => Ids(s.UsgaUnit131, s.UsgaUnit132), Hides: s => Ids(s.TrapUnit131, s.TrapUnit132)),
        ["Motivos (catálogo)"] = new("/api/v1/infraction-types?pageSize=100",
            Sees: s => Ids(s.UsgaInfraction), Hides: s => Ids(s.TrapInfraction)),
        ["Auditoria de acesso"] = new("/api/v1/audit-logs/access?pageSize=200",
            Sees: s => Ids(s.UsgaRegistrarId),
            Hides: s => Ids(s.TrapRegistrarId, s.Usga131.Id, s.Usga132.Id, s.Usga131.EmployeeId, s.Usga132.EmployeeId, s.Trap131.Id, s.Trap132.Id)),
        ["Auditoria de acesso (seletor de usuários)"] = new("/api/v1/audit-logs/access/users?search=pm.so-",
            Sees: _ => [S.UsgaRegistrar], Hides: _ => [S.TrapAdmin, S.TrapRegistrar, S.TrapApprover]),
        ["Auditoria (trilha geral, com medidas)"] = Forbidden("/api/v1/audit-logs?pageSize=200"),
        ["Auditoria (trilha de uma medida da USGA)"] = Forbidden("/api/v1/audit-logs?pageSize=200&reference={Usga132Number}"),
        ["Auditoria (linha do tempo de uma medida)"] = Forbidden("/api/v1/disciplinary-cases/{Usga132}/audit-trail"),
        ["Auditoria (seletor de usuários da trilha geral)"] = Forbidden("/api/v1/audit-logs/users?search=pm.so-"),
        ["Pendentes de aprovação (contador)"] = Forbidden("/api/v1/disciplinary-records/pending-count"),
    };

    /// <summary>Complementar inverso: Gestor da USGA131 na USGA, Administrador na Trapiche.</summary>
    private static readonly Dictionary<string, Expectation> ManagerUsgaAdminTrap = new(StringComparer.Ordinal)
    {
        ["Medidas (lista)"] = new("/api/v1/disciplinary-records?pageSize=100", OnlyUnit: OnlyUsga131, OtherCompanyAppears: false,
            Sees: s => Ids(s.Usga131.Id), Hides: s => Ids(s.Usga132.Id, s.Trap131.Id, s.Trap132.Id)),
        ["Medidas (detalhe, TRAP132)"] = new("/api/v1/disciplinary-records/{Trap132}", HttpStatusCode.NotFound),
        ["Medidas (filtros: gerências e setores)"] = new("/api/v1/disciplinary-records/options",
            Sees: s => Ids(s.UsgaUnit131), Hides: s => Ids(s.UsgaUnit132, s.TrapUnit131, s.TrapUnit132)),
        ["Anexo (download, TRAP132)"] = new("/api/v1/disciplinary-cases/{Trap132}/evidences/{Trap132Evidence}/file", HttpStatusCode.NotFound),
        ["Relatórios (indicadores)"] = new("/api/v1/disciplinary-records/stats", Count: Total, ExpectedCount: 1),
        ["Painel (visão geral)"] = new("/api/v1/disciplinary-records/overview",
            Sees: s => Ids(s.Usga131.Id), Hides: s => Ids(s.Usga132.Id, s.Trap131.Id, s.Trap132.Id)),
        ["Exportação (CSV)"] = new("/api/v1/disciplinary-records/export",
            Sees: _ => [S.EmployeeUsga131], Hides: _ => [S.EmployeeUsga132, S.EmployeeTrap131, S.EmployeeTrap132]),
        ["Funcionários (lista)"] = new("/api/v1/employees?pageSize=100", AllPages: true, OnlyUnit: OnlyUsga131, OtherCompanyAppears: false,
            Sees: _ => [S.EmployeeUsga131], Hides: _ => [S.EmployeeUsga132, S.EmployeeTrap131, .. Trap132Employees]),
        ["Funcionários (detalhe, TRAP131)"] = new("/api/v1/employees/{Trap131Employee}", HttpStatusCode.NotFound),
        ["Funcionários (histórico/apoio à decisão, TRAP131)"] = new("/api/v1/employees/{Trap131Employee}/decision-support", HttpStatusCode.NotFound),
        ["Avisos (lista)"] = Forbidden("/api/v1/notifications?pageSize=100"),
        ["Avisos (contador de não lidos)"] = Forbidden("/api/v1/notifications/unread/count"),

        ["Lotes de importação de funcionários"] = new("/api/v1/employees/import/batches?pageSize=100",
            Sees: s => Ids(s.TrapEmployeeBatch), Hides: s => [s.UsgaEmployeeBatch.ToString(), s.UsgaName]),
        ["Lotes de importação do histórico"] = new("/api/v1/disciplinary-history/import/batches?pageSize=100",
            Sees: s => Ids(s.TrapHistoryBatch), Hides: s => [s.UsgaHistoryBatch.ToString(), s.UsgaName]),
        ["Usuários (lista)"] = new("/api/v1/users?pageSize=100",
            Sees: _ => [S.TrapAdmin, S.TrapRegistrar, S.TrapApprover, S.RegistrarUsgaManagerTrap],
            Hides: s => [S.UsgaRegistrar, S.UsgaApprover, "admin@sigh.com", s.UsgaName]),
        ["Usuários (detalhe, só da Trapiche)"] = new("/api/v1/users/{TrapRegistrarId}"),
        ["Usuários (detalhe, só da USGA)"] = new("/api/v1/users/{UsgaRegistrarId}", HttpStatusCode.NotFound),
        ["Usuários (perfis e gerências de quem está nas duas)"] = new("/api/v1/users/{RegistrarUsgaManagerTrapId}/access",
            Sees: s => Ids(s.TrapUnit131, DatabaseSeeder.TrapicheCompanyId), Hides: s => [DatabaseSeeder.DefaultCompanyId.ToString(), s.UsgaName]),
        ["Usuários (situação dos administradores)"] = new("/api/v1/users/admin-status",
            Sees: _ => [CompanyDefaults.TrapicheCode], Hides: s => [CompanyDefaults.UsgaCode, s.UsgaName]),
        ["Gerências (administração)"] = new("/api/v1/management-units",
            Sees: s => Ids(s.TrapUnit131, s.TrapUnit132), Hides: s => Ids(s.UsgaUnit131, s.UsgaUnit132)),
        ["Motivos (catálogo)"] = new("/api/v1/infraction-types?pageSize=100",
            Sees: s => Ids(s.TrapInfraction), Hides: s => Ids(s.UsgaInfraction)),
        ["Auditoria de acesso"] = new("/api/v1/audit-logs/access?pageSize=200",
            Sees: s => Ids(s.TrapRegistrarId),
            Hides: s => Ids(s.UsgaRegistrarId, s.Trap131.Id, s.Trap132.Id, s.Trap131.EmployeeId, s.Trap132.EmployeeId, s.Usga131.Id, s.Usga132.Id)),
        ["Auditoria de acesso (seletor de usuários)"] = new("/api/v1/audit-logs/access/users?search=pm.so-",
            Sees: _ => [S.TrapRegistrar], Hides: _ => [S.UsgaRegistrar, S.UsgaApprover]),
        ["Auditoria (trilha geral, com medidas)"] = Forbidden("/api/v1/audit-logs?pageSize=200"),
        ["Auditoria (trilha de uma medida da Trapiche)"] = Forbidden("/api/v1/audit-logs?pageSize=200&reference={Trap132Number}"),
        ["Auditoria (linha do tempo de uma medida)"] = Forbidden("/api/v1/disciplinary-cases/{Trap132}/audit-trail"),
        ["Auditoria (seletor de usuários da trilha geral)"] = Forbidden("/api/v1/audit-logs/users?search=pm.so-"),
        ["Pendentes de aprovação (contador)"] = Forbidden("/api/v1/disciplinary-records/pending-count"),
    };

    internal static readonly IReadOnlyDictionary<string, Dictionary<string, Expectation>> Cases = new Dictionary<string, Dictionary<string, Expectation>>(StringComparer.Ordinal)
    {
        [S.RegistrarUsgaManagerTrap] = RegistrarUsgaManagerTrap,
        [S.ManagerUsgaRegistrarTrap] = ManagerUsgaRegistrarTrap,
        [S.AdminUsgaManagerTrap] = AdminUsgaManagerTrap,
        [S.ManagerUsgaAdminTrap] = ManagerUsgaAdminTrap,
    };

    public static IEnumerable<object[]> Rows() =>
        Cases.SelectMany(user => user.Value.Keys.Select(listing => new object[] { user.Key, listing }));

    private static string Resolve(string url, S s) => url
        .Replace("{Usga131}", s.Usga131.Id.ToString()).Replace("{Usga132}", s.Usga132.Id.ToString())
        .Replace("{Trap131}", s.Trap131.Id.ToString()).Replace("{Trap132}", s.Trap132.Id.ToString())
        .Replace("{Usga132Evidence}", s.Usga132.EvidenceId.ToString()).Replace("{Trap132Evidence}", s.Trap132.EvidenceId.ToString())
        .Replace("{Usga131Employee}", s.Usga131.EmployeeId.ToString()).Replace("{Usga132Employee}", s.Usga132.EmployeeId.ToString())
        .Replace("{Trap131Employee}", s.Trap131.EmployeeId.ToString()).Replace("{Trap132Employee}", s.Trap132.EmployeeId.ToString())
        .Replace("{Usga131Number}", s.Usga131.Number).Replace("{Usga132Number}", s.Usga132.Number)
        .Replace("{Trap131Number}", s.Trap131.Number).Replace("{Trap132Number}", s.Trap132.Number)
        .Replace("{UsgaRegistrarId}", s.UsgaRegistrarId.ToString()).Replace("{TrapRegistrarId}", s.TrapRegistrarId.ToString())
        .Replace("{ManagerUsgaRegistrarTrapId}", s.ManagerUsgaRegistrarTrapId.ToString())
        .Replace("{RegistrarUsgaManagerTrapId}", s.RegistrarUsgaManagerTrapId.ToString());

    /// <summary>GET; com <see cref="Expectation.AllPages"/>, todas as páginas (um corpo por página).</summary>
    private static async Task<(HttpStatusCode Status, List<string> Pages)> FetchAsync(HttpClient client, string url, bool allPages)
    {
        var response = await client.GetAsync(url);
        var pages = new List<string> { await response.Content.ReadAsStringAsync() };
        if (!allPages || response.StatusCode != HttpStatusCode.OK)
            return (response.StatusCode, pages);

        var total = JsonDocument.Parse(pages[0]).RootElement.GetProperty("data").GetProperty("totalPages").GetInt32();
        for (var page = 2; page <= total; page++)
        {
            var next = await client.GetAsync($"{url}&pageNumber={page}");
            next.StatusCode.Should().Be(HttpStatusCode.OK);
            pages.Add(await next.Content.ReadAsStringAsync());
        }
        return (response.StatusCode, pages);
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task InAllCompaniesMode_EachCompanyShowsOnlyWhatItsOwnProfileAllows(string user, string listing)
    {
        var scenario = await S.GetAsync(_factory);
        var expected = Cases[user][listing];
        using var client = await AccessTestHelpers.LoginAsync(_factory, user, ICompanyContext.AllCompanies);

        var url = Resolve(expected.Url, scenario);
        var (status, pages) = await FetchAsync(client, url, expected.AllPages);
        var body = string.Concat(pages);

        status.Should().Be(expected.Status, $"{listing} para {user} (GET {url}): {body}");
        if (status != HttpStatusCode.OK)
            return;

        foreach (var marker in expected.Sees?.Invoke(scenario) ?? [])
            body.Should().Contain(marker, $"{listing} para {user}: a permissão da empresa permite ver {marker}");
        foreach (var marker in expected.Hides?.Invoke(scenario) ?? [])
            body.Should().NotContain(marker, $"{listing} para {user}: VAZAMENTO, {marker} não é permitido pela permissão daquela empresa");
        if (expected.Count is { } read)
            read(JsonDocument.Parse(body).RootElement.GetProperty("data")).Should().Be(expected.ExpectedCount, $"{listing} para {user}");
        if (expected.OnlyUnit is { } only)
        {
            var items = pages.SelectMany(p => JsonDocument.Parse(p).RootElement.GetProperty("data").GetProperty("items").EnumerateArray()).ToList();
            var ofManagerCompany = items.Where(i => i.GetProperty("companyId").GetGuid() == only.CompanyId).ToList();
            ofManagerCompany.Should().NotBeEmpty($"{listing} para {user}: a gerência {only.UnitCode} tem itens");
            ofManagerCompany.Select(i => i.GetProperty("managementUnitCode").GetString()).Distinct().Should()
                .Equal([only.UnitCode], $"{listing} para {user}: na empresa em que é Gestor, só a gerência {only.UnitCode}");
            if (expected.OtherCompanyAppears)
                items.Should().Contain(i => i.GetProperty("companyId").GetGuid() != only.CompanyId,
                    $"{listing} para {user}: a outra empresa (perfil com escopo total) também aparece");
            else
                items.Should().OnlyContain(i => i.GetProperty("companyId").GetGuid() == only.CompanyId,
                    $"{listing} para {user}: na empresa em que é Administrador não há dado de funcionário nem de medida");
        }
    }

    /// <summary>
    /// O modo "Todas" é a união das empresas, cada uma com a própria permissão: a lista de medidas e o total do
    /// relatório no modo "Todas" batem com a soma das duas empresas consultadas uma a uma.
    /// </summary>
    [Theory]
    [InlineData(S.RegistrarUsgaManagerTrap)]
    [InlineData(S.ManagerUsgaRegistrarTrap)]
    [InlineData(S.AdminUsgaManagerTrap)]
    [InlineData(S.ManagerUsgaAdminTrap)]
    public async Task AllCompaniesMode_IsTheUnionOfEachCompanyWithItsOwnProfile(string user)
    {
        await S.GetAsync(_factory);

        async Task<(HashSet<string> Ids, int Total)> ReadAsync(string company)
        {
            using var client = await AccessTestHelpers.LoginAsync(_factory, user, company);
            // Empresa em que a pessoa é só Administrador: nenhuma medida (403), que conta como vazio.
            if ((await client.GetAsync("/api/v1/disciplinary-records?pageSize=1")).StatusCode == HttpStatusCode.Forbidden)
                return ([], 0);
            var list = JsonDocument.Parse(await client.GetStringAsync("/api/v1/disciplinary-records?pageSize=100")).RootElement.GetProperty("data");
            var stats = JsonDocument.Parse(await client.GetStringAsync("/api/v1/disciplinary-records/stats")).RootElement.GetProperty("data");
            return (list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToHashSet(), Total(stats));
        }

        var usga = await ReadAsync(CompanyDefaults.UsgaCode);
        var trap = await ReadAsync(CompanyDefaults.TrapicheCode);
        var all = await ReadAsync(ICompanyContext.AllCompanies);

        all.Ids.Should().BeEquivalentTo(usga.Ids.Union(trap.Ids), $"modo Todas de {user} = USGA ∪ Trapiche");
        all.Total.Should().Be(usga.Total + trap.Total);
    }
}
