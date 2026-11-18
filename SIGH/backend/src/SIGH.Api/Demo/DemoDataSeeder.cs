using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIGH.Application.Authentication.ChangePassword;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Disciplinary.DisciplinaryCases.AddEvidence;
using SIGH.Application.Disciplinary.Evidences;
using SIGH.Application.Disciplinary.InfractionTypes.CreateInfractionType;
using SIGH.Application.Disciplinary.Records;
using SIGH.Application.Employees.Import;
using SIGH.Application.Interfaces;
using SIGH.Application.Users.Access;
using SIGH.Application.Users.CreateUser;
using SIGH.Domain.Disciplinary.Enums;
using SIGH.Domain.Repositories;
using SIGH.Persistence.Context;

namespace SIGH.Api.Demo;

/// <summary>
/// Dados de demonstração FICTÍCIOS, criados pelos SERVIÇOS REAIS (importador de funcionários, criação de
/// usuário, troca de senha, registro/aprovação/rejeição/cancelamento de medidas e upload de evidência),
/// nunca por inserção direta no banco. Cada etapa roda "como" o usuário certo (o admin importa e cria
/// usuários; o auxiliar registra; o encarregado aprova), com as permissões efetivas dele.
/// Idempotente: cada parte confere se já existe antes de criar.
/// </summary>
public sealed class DemoDataSeeder
{
    public const string Domain = "demo.local";

    /// <param name="Company">Empresa em que a conta é criada e os perfis valem (USGA ou TRAP).</param>
    /// <param name="AlsoIn">Outras empresas em que a MESMA conta recebe os mesmos perfis (vínculo de usuário existente).</param>
    public sealed record DemoUser(string Login, string FullName, string Cpf, string[] Roles, string[] Units,
        string Company = CompanyDefaults.UsgaCode, string[]? AlsoIn = null);

    /// <summary>
    /// Usuários de demonstração (e-mail = login@demo.local). CPFs sintéticos válidos. Os antigos (auxiliar, encarregado,
    /// gestor.industrial, gestor.agricola, preposto) continuam apontando para a USGA; os novos são um conjunto por empresa;
    /// o "juridico" tem o perfil Jurídico nas duas empresas.
    /// </summary>
    public static readonly IReadOnlyList<DemoUser> Users =
    [
        new("auxiliar", "AUXILIAR DE ESCRITORIO DEMO TESTE", SyntheticCpf("900000001"), [SystemRoles.HrRegistrar], []),
        new("encarregado", "ENCARREGADO DO RH DEMO TESTE", SyntheticCpf("900000002"), [SystemRoles.HrApprover], []),
        new("juridico", "ADVOGADA DO JURIDICO DEMO TESTE", SyntheticCpf("900000003"), [SystemRoles.Legal], [], AlsoIn: [CompanyDefaults.TrapicheCode]),
        new("gestor.industrial", "GESTOR DA INDUSTRIAL DEMO TESTE", SyntheticCpf("900000004"), [SystemRoles.Manager], ["USGA131"]),
        new("gestor.agricola", "GESTOR AGRICOLA DEMO TESTE", SyntheticCpf("900000005"), [SystemRoles.Manager], ["USGA137", "USGA141"]),
        new("preposto", "PREPOSTO DO RH DEMO TESTE", SyntheticCpf("900000006"), [SystemRoles.HrRegistrar, SystemRoles.HrApprover], []),
        // Por empresa: USGA.
        new("admin.usga", "ADMINISTRADOR DA USGA DEMO TESTE", SyntheticCpf("900000007"), [SystemRoles.Administrator], []),
        new("auxiliar.usga", "AUXILIAR DA USGA DEMO TESTE", SyntheticCpf("900000008"), [SystemRoles.HrRegistrar], []),
        new("encarregado.usga", "ENCARREGADO DA USGA DEMO TESTE", SyntheticCpf("900000009"), [SystemRoles.HrApprover], []),
        new("gestor.usga131", "GESTOR DA USGA131 DEMO TESTE", SyntheticCpf("900000010"), [SystemRoles.Manager], ["USGA131"]),
        // Por empresa: Trapiche (o admin.trap é o primeiro administrador da Trapiche na demonstração).
        new("auxiliar.trap", "AUXILIAR DA TRAPICHE DEMO TESTE", SyntheticCpf("900000012"), [SystemRoles.HrRegistrar], [], CompanyDefaults.TrapicheCode),
        new("encarregado.trap", "ENCARREGADO DA TRAPICHE DEMO TESTE", SyntheticCpf("900000013"), [SystemRoles.HrApprover], [], CompanyDefaults.TrapicheCode),
        new("gestor.trap131", "GESTOR DA TRAP131 DEMO TESTE", SyntheticCpf("900000014"), [SystemRoles.Manager], ["TRAP131"], CompanyDefaults.TrapicheCode),
    ];

    /// <summary>Primeiro administrador da Trapiche na demonstração (na instalação real vem de Seed:Companies:TRAP:AdminEmail).</summary>
    public const string TrapicheAdminLogin = "admin.trap";
    private static readonly string TrapicheAdminCpf = SyntheticCpf("900000011");

    private enum Outcome { Approved, Pending, Rejected, Cancelled }

    private sealed record DemoRecord(int Number, string EmployeeNumber, DisciplinaryMeasureType Type, string Infraction, int DaysAgo, Outcome Outcome,
        int SuspensionDays = 0, bool WithAttachment = false);

    /// <summary>Medidas fictícias da Trapiche em vários estados (registradas pelo auxiliar.trap, revistas pelo encarregado.trap).</summary>
    private static readonly DemoRecord[] TrapicheRecords =
    [
        new(1, "70090001", DisciplinaryMeasureType.WrittenWarning, "FALTA_INJUSTIFICADA", 14, Outcome.Approved, WithAttachment: true), // TRAP131
        new(2, "70090003", DisciplinaryMeasureType.Suspension, "EPI", 10, Outcome.Approved, SuspensionDays: 1),                      // TRAP131
        new(3, "70090007", DisciplinaryMeasureType.VerbalWarning, "ATRASO", 5, Outcome.Pending),                                      // TRAP132
        new(4, "70090009", DisciplinaryMeasureType.WrittenWarning, "EPI", 7, Outcome.Rejected),                                       // TRAP132
        new(5, "70090011", DisciplinaryMeasureType.VerbalWarning, "ATRASO", 8, Outcome.Cancelled),                                    // TRAP132
    ];

    /// <summary>3 aprovadas, 1 suspensão aprovada, 2 pendentes, 1 rejeitada aguardando correção e 1 cancelada.</summary>
    private static readonly DemoRecord[] Records =
    [
        new(1, "50090006", DisciplinaryMeasureType.WrittenWarning, "EPI", 20, Outcome.Approved, WithAttachment: true), // USGA131
        new(2, "50090000", DisciplinaryMeasureType.VerbalWarning, "ATRASO", 18, Outcome.Approved),                     // USGA132
        new(3, "50090001", DisciplinaryMeasureType.WrittenWarning, "FALTA_INJUSTIFICADA", 15, Outcome.Approved),       // USGA141
        new(4, "50090002", DisciplinaryMeasureType.Suspension, "FALTA_INJUSTIFICADA", 12, Outcome.Approved, SuspensionDays: 2), // USGA141
        new(5, "50090012", DisciplinaryMeasureType.WrittenWarning, "ATRASO", 6, Outcome.Pending),                      // USGA131
        new(6, "50090003", DisciplinaryMeasureType.VerbalWarning, "FALTA_INJUSTIFICADA", 4, Outcome.Pending),          // USGA136
        new(7, "50090009", DisciplinaryMeasureType.WrittenWarning, "EPI", 9, Outcome.Rejected),                        // USGA136
        new(8, "50090015", DisciplinaryMeasureType.VerbalWarning, "ATRASO", 10, Outcome.Cancelled),                    // USGA134
    ];

    /// <summary>
    /// Catálogo da demonstração com os nomes da planilha de penalidades: assim a importação do histórico só cria
    /// o motivo novo "USO DE CELULAR EM SERVIÇO", como em HISTORICO.md.
    /// </summary>
    private static readonly (string Code, string Name, InfractionSeverity Severity)[] InfractionTypes =
    [
        ("FALTA_INJUSTIFICADA", "Faltas injustificadas de forma recorrente", InfractionSeverity.Moderate),
        ("EPI", "Falta de utilização do EPI", InfractionSeverity.High),
        ("NEGLIGENCIA", "Negligência ou imprudência no desempenho de suas funções", InfractionSeverity.High),
        ("ATRASO", "Atrasos reiterados", InfractionSeverity.Low),
    ];

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly DemoDataOptions _options;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<DemoDataSeeder> _logger;

    public DemoDataSeeder(IServiceScopeFactory scopes, IConfiguration configuration, IOptions<DemoDataOptions> options, IDateTimeProvider clock, ILogger<DemoDataSeeder> logger)
    {
        _scopes = scopes;
        _configuration = configuration;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public static string EmailOf(string login) => $"{login}@{Domain}";

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var adminEmail = _configuration["Seed:AdminEmail"]?.Trim().ToLowerInvariant();
            var adminId = await UserIdAsync(adminEmail, cancellationToken)
                          ?? throw new InvalidOperationException("Dados de demonstração exigem o administrador do seed (Seed:AdminEmail e Seed:AdminPassword).");
            const string usga = CompanyDefaults.UsgaCode;
            const string trap = CompanyDefaults.TrapicheCode;

            // USGA: os arquivos fictícios do pacote de referência.
            var usgaEmployees = await File.ReadAllBytesAsync(Path.Combine(DataPath(), "importacao-funcionarios", "funcionarios_carga_inicial.xlsx"), cancellationToken);
            await ImportEmployeesAsync(adminId, usga, usgaEmployees, "funcionarios_carga_inicial.xlsx", cancellationToken);
            var infractions = new Dictionary<string, Dictionary<string, Guid>>
            {
                [usga] = await EnsureInfractionTypesAsync(adminId, usga, cancellationToken)
            };
            var usgaHistory = await File.ReadAllBytesAsync(Path.Combine(DataPath(), "historico-disciplinar", "base_disciplinar_ficticia.xlsx"), cancellationToken);
            await ImportHistoryAsync(adminId, usga, usgaHistory, "base_disciplinar_ficticia.xlsx", cancellationToken);

            // Trapiche: planilhas FICTÍCIAS geradas por código (TRAP131/TRAP132, nomes TESTE).
            var trapAdminId = await EnsureTrapicheAdminAsync(cancellationToken);
            await ImportEmployeesAsync(trapAdminId, trap, TrapicheDemoSpreadsheets.EmployeesXlsx(), "funcionarios_trapiche_ficticio.xlsx", cancellationToken);
            infractions[trap] = await EnsureInfractionTypesAsync(trapAdminId, trap, cancellationToken);
            await ImportHistoryAsync(trapAdminId, trap, TrapicheDemoSpreadsheets.HistoryXlsx(), "penalidades_trapiche_ficticio.xlsx", cancellationToken);

            var admins = new Dictionary<string, Guid> { [usga] = adminId, [trap] = trapAdminId };
            var users = await EnsureUsersAsync(admins, cancellationToken);
            await EnsureRecordsAsync(usga, "DEMO", Records, users["auxiliar"], users["encarregado"], infractions[usga], cancellationToken);
            await EnsureRecordsAsync(trap, "DEMO-TRAP", TrapicheRecords, users["auxiliar.trap"], users["encarregado.trap"], infractions[trap], cancellationToken);
            _logger.LogInformation("Demo: dados de demonstração prontos ({Users} usuários, {Records} medidas).", users.Count, Records.Length + TrapicheRecords.Length);
        }
        catch (Exception ex)
        {
            // A API continua de pé: a falha fica no log e os scripts locais mostram o aviso.
            _logger.LogError(ex, "Demo: falha ao criar os dados de demonstração.");
        }
    }

    private static Guid CompanyIdOf(string code) => code == CompanyDefaults.TrapicheCode ? CompanyDefaults.TrapicheId : CompanyDefaults.UsgaId;

    // ---- a. funcionários (importador do SAP), para a empresa ativa ----

    private async Task ImportEmployeesAsync(Guid adminId, string company, byte[] bytes, string fileName, CancellationToken ct)
    {
        await RunAsAsync(adminId, company, async sp =>
        {
            var companyId = CompanyIdOf(company);
            var db = sp.GetRequiredService<SighDbContext>();
            if (await db.Employees.AnyAsync(e => e.CompanyId == companyId, ct))
            {
                _logger.LogInformation("Demo: funcionários da {Company} já importados.", company);
                return;
            }

            var import = sp.GetRequiredService<IEmployeeImportService>();
            var preview = await import.PreviewAsync(new MemoryStream(bytes), fileName, adminId, ct);
            if (!preview.Success) throw new InvalidOperationException($"Pré-visualização da carga de funcionários ({company}) falhou: {preview.Message}");
            var confirm = await import.ConfirmAsync(preview.Data!.BatchId, new MemoryStream(bytes), false, adminId, ct);
            if (!confirm.Success) throw new InvalidOperationException($"Confirmação da carga de funcionários ({company}) falhou: {confirm.Message}");
            _logger.LogInformation("Demo: {Count} funcionários fictícios importados na {Company}.", confirm.Data!.Summary.Created, company);
        });
    }

    // ---- b. histórico disciplinar fictício (importador do histórico) ----

    private async Task ImportHistoryAsync(Guid adminId, string company, byte[] bytes, string fileName, CancellationToken ct)
    {
        await RunAsAsync(adminId, company, async sp =>
        {
            var companyId = CompanyIdOf(company);
            var db = sp.GetRequiredService<SighDbContext>();
            if (await db.DisciplinaryCases.AnyAsync(c => c.Kind == DisciplinaryCaseKind.Historical && c.CompanyId == companyId, ct))
            {
                _logger.LogInformation("Demo: histórico disciplinar da {Company} já importado.", company);
                return;
            }

            var import = sp.GetRequiredService<SIGH.Application.Disciplinary.History.IHistoryImportService>();
            if ((await import.GetStatusAsync(ct)).IsClosed)
            {
                _logger.LogInformation("Demo: importação de histórico da {Company} encerrada; etapa pulada.", company);
                return;
            }

            var preview = await import.PreviewAsync(new MemoryStream(bytes), fileName, adminId, ct);
            if (!preview.Success) throw new InvalidOperationException($"Pré-visualização do histórico ({company}) falhou: {preview.Message}");
            var confirm = await import.ConfirmAsync(preview.Data!.BatchId, new MemoryStream(bytes), adminId, ct);
            if (!confirm.Success) throw new InvalidOperationException($"Confirmação do histórico ({company}) falhou: {confirm.Message}");
            _logger.LogInformation("Demo: {Count} registros históricos fictícios importados na {Company}.", confirm.Data!.Imported, company);
        });
    }

    // ---- motivos (cada empresa tem o próprio catálogo) ----

    private async Task<Dictionary<string, Guid>> EnsureInfractionTypesAsync(Guid adminId, string company, CancellationToken ct)
    {
        var result = new Dictionary<string, Guid>();
        await RunAsAsync(adminId, company, async sp =>
        {
            var companyId = CompanyIdOf(company);
            var db = sp.GetRequiredService<SighDbContext>();
            foreach (var (code, name, severity) in InfractionTypes)
            {
                var id = await db.InfractionTypes.Where(i => i.Code == code && i.CompanyId == companyId).Select(i => (Guid?)i.Id).FirstOrDefaultAsync(ct);
                if (id is null)
                {
                    var created = await sp.GetRequiredService<ICreateInfractionTypeUseCase>().ExecuteAsync(new CreateInfractionTypeRequest(code, name, severity), ct);
                    if (!created.Success) throw new InvalidOperationException($"Motivo {code} ({company}): {created.Message}");
                    id = created.Data!.Id;
                }
                result[code] = id.Value;
            }
        });
        return result;
    }

    // ---- c. usuários (criação real + troca de senha real: sem troca obrigatória depois) ----

    /// <summary>
    /// Primeiro administrador da Trapiche na demonstração, criado como o seed cria o de cada empresa
    /// (Seed:Companies:&lt;código&gt;:AdminEmail), mas já com a senha de demonstração.
    /// </summary>
    private async Task<Guid> EnsureTrapicheAdminAsync(CancellationToken ct)
    {
        var email = EmailOf(TrapicheAdminLogin);
        if (await UserIdAsync(email, ct) is { } existing)
            return existing;

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var adminRole = await db.Roles.SingleAsync(r => r.Name == SystemRoles.Administrator, ct);
        var hash = hasher.HashPassword(_options.DemoPassword!);
        var admin = new SIGH.Domain.Entities.User
        {
            FullName = "ADMINISTRADOR DA TRAPICHE DEMO TESTE",
            Email = email,
            Cpf = TrapicheAdminCpf,
            PasswordHash = hash,
            Status = SIGH.Domain.Enums.UserStatus.Active,
            MustChangePassword = false,
            CreatedInCompanyId = CompanyDefaults.TrapicheId
        };
        admin.UserRoles.Add(new SIGH.Domain.Entities.UserRole { UserId = admin.Id, RoleId = adminRole.Id, CompanyId = CompanyDefaults.TrapicheId });
        admin.PasswordHistories.Add(new SIGH.Domain.Entities.PasswordHistory { UserId = admin.Id, PasswordHash = hash });
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Demo: administrador {Email} da Trapiche criado.", email);
        return admin.Id;
    }

    private async Task<Dictionary<string, Guid>> EnsureUsersAsync(IReadOnlyDictionary<string, Guid> admins, CancellationToken ct)
    {
        var ids = new Dictionary<string, Guid> { [TrapicheAdminLogin] = admins[CompanyDefaults.TrapicheCode] };
        foreach (var user in Users)
        {
            var email = EmailOf(user.Login);
            var adminId = admins[user.Company];
            var id = await UserIdAsync(email, ct) ?? Guid.Empty;
            if (id == Guid.Empty)
            {
                var initialPassword = RandomPassword();
                await RunAsAsync(adminId, user.Company, async sp =>
                {
                    var db = sp.GetRequiredService<SighDbContext>();
                    var companyId = CompanyIdOf(user.Company);
                    var roles = await db.Roles.Where(r => user.Roles.Contains(r.Name)).Select(r => r.Id).ToListAsync(ct);
                    var units = await db.ManagementUnits.Where(m => m.CompanyId == companyId && user.Units.Contains(m.Code!)).Select(m => m.Id).ToListAsync(ct);
                    var created = await sp.GetRequiredService<ICreateUserService>()
                        .CreateUserAsync(new CreateUserRequest(user.FullName, email, user.Cpf, initialPassword, roles, units), ct);
                    id = created.Id;
                });

                // O próprio usuário "faz o primeiro acesso": troca a senha inicial pela senha de demonstração.
                await RunAsAsync(id, user.Company, async sp =>
                    await sp.GetRequiredService<IChangePasswordService>().ChangePasswordAsync(new ChangePasswordRequest(id, initialPassword, _options.DemoPassword!), ct));
                _logger.LogInformation("Demo: usuário {Email} criado na {Company}.", email, user.Company);
            }

            // A mesma conta em outra empresa: vínculo pelo administrador dela (sem criar outra conta).
            foreach (var other in user.AlsoIn ?? [])
            {
                var otherId = CompanyIdOf(other);
                await RunAsAsync(admins[other], other, async sp =>
                {
                    var db = sp.GetRequiredService<SighDbContext>();
                    if (await db.UserRoles.AnyAsync(ur => ur.UserId == id && ur.CompanyId == otherId, ct))
                        return;
                    var roles = await db.Roles.Where(r => user.Roles.Contains(r.Name)).Select(r => r.Id).ToListAsync(ct);
                    await sp.GetRequiredService<IUserAccessAdministrationService>()
                        .LinkExistingUserAsync(admins[other], new LinkExistingUserRequest(email, roles), ct);
                    _logger.LogInformation("Demo: usuário {Email} vinculado à {Company}.", email, other);
                });
            }

            ids[user.Login] = id;
        }
        return ids;
    }

    // ---- d. medidas em todos os estados ----

    private async Task EnsureRecordsAsync(string company, string tagPrefix, IReadOnlyList<DemoRecord> records, Guid registrar, Guid approver,
        IReadOnlyDictionary<string, Guid> infractions, CancellationToken ct)
    {
        var today = BusinessCalendar.Today(_clock.UtcNow);
        var companyId = CompanyIdOf(company);

        foreach (var demo in records)
        {
            var tag = $"[{tagPrefix}-{demo.Number}]";
            Guid? existing = null;
            await RunAsAsync(registrar, company, async sp =>
                existing = await sp.GetRequiredService<SighDbContext>().DisciplinaryCases
                    .Where(c => c.Kind == DisciplinaryCaseKind.Record && c.CompanyId == companyId && c.Description.Contains(tag)).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct));
            if (existing is not null)
                continue;

            var occurrence = today.AddDays(-demo.DaysAgo);
            var measureDate = occurrence.AddDays(1);
            var input = new RecordInput(
                await EmployeeIdAsync(companyId, demo.EmployeeNumber, ct),
                infractions[demo.Infraction],
                demo.Type,
                [occurrence],
                measureDate,
                demo.Type == DisciplinaryMeasureType.Suspension ? measureDate.AddDays(1) : null,
                demo.Type == DisciplinaryMeasureType.Suspension ? measureDate.AddDays(demo.SuspensionDays) : null,
                $"{tag} Medida fictícia de demonstração: {DescriptionOf(demo)}",
                ConfirmPossibleDuplicate: true);

            Guid recordId = Guid.Empty;
            await RunAsAsync(registrar, company, async sp =>
                recordId = (await sp.GetRequiredService<IRecordService>().RegisterAsync(registrar, input, ct)).Record!.Summary.Id);

            if (demo.WithAttachment)
                await AttachLetterAsync(registrar, company, recordId, ct);

            switch (demo.Outcome)
            {
                case Outcome.Approved:
                    await RunAsAsync(approver, company, sp => sp.GetRequiredService<IRecordService>().ApproveAsync(approver, recordId, ct));
                    break;
                case Outcome.Rejected:
                    await RunAsAsync(approver, company, sp => sp.GetRequiredService<IRecordService>().RejectAsync(approver, recordId,
                        new ReasonInput("Data da ocorrência não confere com o cartão de ponto (demonstração)."), ct));
                    break;
                case Outcome.Cancelled:
                    await RunAsAsync(approver, company, sp => sp.GetRequiredService<IRecordService>().RejectAsync(approver, recordId,
                        new ReasonInput("Registrada para o funcionário errado (demonstração)."), ct));
                    await RunAsAsync(registrar, company, sp => sp.GetRequiredService<IRecordService>().CancelAsync(registrar, recordId,
                        new ReasonInput("Cancelada pelo registrante após a rejeição (demonstração)."), ct));
                    break;
            }
            _logger.LogInformation("Demo: medida {Tag} criada ({Outcome}).", tag, demo.Outcome);
        }
    }

    /// <summary>Carta assinada fictícia: um PDF gerado na hora, anexado pelos serviços de evidência.</summary>
    private Task AttachLetterAsync(Guid registrar, string company, Guid recordId, CancellationToken ct) =>
        RunAsAsync(registrar, company, async sp =>
        {
            var evidence = await sp.GetRequiredService<IAddEvidenceUseCase>().ExecuteAsync(
                new AddEvidenceRequest(recordId, EvidenceType.Document, "Carta assinada (fictícia)", registrar, _clock.UtcNow,
                    Description: "Documento fictício gerado para a demonstração."), ct);
            if (!evidence.Success) throw new InvalidOperationException($"Anexo: {evidence.Message}");

            var pdf = FictitiousPdf();
            var upload = await sp.GetRequiredService<IUploadEvidenceFileUseCase>().ExecuteAsync(
                new UploadEvidenceFileRequest(recordId, evidence.Data!.Id, new MemoryStream(pdf), "carta-assinada-ficticia.pdf", pdf.Length, registrar), ct);
            if (!upload.Success) throw new InvalidOperationException($"Upload do anexo: {upload.Message}");
        });

    private static string DescriptionOf(DemoRecord demo) => demo.Infraction switch
    {
        "EPI" => "trabalhou sem os óculos de proteção na área da moenda.",
        "ATRASO" => "chegou atrasado ao turno em dias seguidos.",
        _ => "faltou ao turno sem justificativa."
    };

    // ---- execução "como" um usuário, numa empresa (serviços reais com as permissões efetivas dele nela) ----

    /// <summary>
    /// Cria um escopo com um HttpContext cujo usuário é <paramref name="userId"/> e cuja empresa ativa (cabeçalho
    /// X-SIGH-Company) é <paramref name="company"/>. CurrentUserService, contexto de empresa, escopo e auditoria
    /// funcionam como numa requisição desse usuário: as permissões vêm do banco, por empresa.
    /// </summary>
    private async Task RunAsAsync(Guid userId, string company, Func<IServiceProvider, Task> action)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var user = await sp.GetRequiredService<IUserRepository>().GetByIdAsync(userId)
                   ?? throw new InvalidOperationException($"Usuário {userId} não encontrado.");

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Email, user.Email), new(ClaimTypes.Name, user.FullName) };

        var accessor = sp.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        var context = new DefaultHttpContext
        {
            RequestServices = sp,
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "DemoSeed")),
        };
        context.Request.Headers[ICompanyContext.HeaderName] = company;
        accessor.HttpContext = context;
        try
        {
            await action(sp);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    private async Task<Guid?> UserIdAsync(string? email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        await using var scope = _scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SighDbContext>().Users
            .Where(u => u.Email == email).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }

    private async Task<Guid> EmployeeIdAsync(Guid companyId, string employeeNumber, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SighDbContext>().Employees
            .Where(e => e.CompanyId == companyId && e.EmployeeNumber == employeeNumber).Select(e => e.Id).SingleAsync(ct);
    }

    private string DataPath() =>
        !string.IsNullOrWhiteSpace(_options.DemoDataPath)
            ? _options.DemoDataPath
            : throw new InvalidOperationException("Seed:DemoDataPath não configurado (pasta SIGH/docs/referencias).");

    /// <summary>Senha inicial aleatória (descartada: o usuário troca logo em seguida).</summary>
    private static string RandomPassword() => $"Ini#{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}a";

    public static string SyntheticCpf(string base9)
    {
        var digits = base9.Select(c => c - '0').ToList();
        for (var round = 0; round < 2; round++)
        {
            var weight = digits.Count + 1;
            var rest = digits.Sum(d => d * weight--) % 11;
            digits.Add(rest < 2 ? 0 : 11 - rest);
        }
        return string.Concat(digits);
    }

    /// <summary>PDF mínimo e válido, com o texto "DOCUMENTO FICTICIO - DEMONSTRACAO".</summary>
    public static byte[] FictitiousPdf()
    {
        const string content = "BT /F1 18 Tf 40 120 Td (DOCUMENTO FICTICIO - DEMONSTRACAO) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
