using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIGH.Persistence.Migrations
{
    /// <summary>
    /// Multiempresa (USGA e Trapiche). Os dados existentes (perfis, usuários, motivos, lotes, estado da importação do
    /// histórico e trilha de auditoria) vão para a USGA, o Id da antiga empresa padrão; a Trapiche é criada e recebe
    /// uma cópia dos motivos ativos da USGA. Índices únicos passam a ser por empresa (matrícula, CPF, código da
    /// gerência, código do motivo, chave do histórico; perfil por usuário + perfil + empresa).
    /// </summary>
    public partial class MultiCompany : Migration
    {
        private const string UsgaId = "00000000-0000-0000-0000-000000000001";
        private const string TrapicheId = "00000000-0000-0000-0000-000000000002";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserRoles_UserId_RoleId",
                table: "UserRoles");

            migrationBuilder.DropIndex(
                name: "IX_ManagementUnits_CompanyId_Code",
                table: "ManagementUnits");

            migrationBuilder.DropIndex(
                name: "UX_InfractionTypes_Code",
                table: "InfractionTypes");

            migrationBuilder.DropIndex(
                name: "IX_Employees_Cpf",
                table: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_DisciplinaryCases_HistoryKey",
                table: "DisciplinaryCases");

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedInCompanyId",
                table: "Users",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "UserRoles",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "InfractionTypes",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "HistoryImportStates",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "EmployeeImportBatches",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "DisciplinaryHistoryImportBatches",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "Companies",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ManagementUnitPrefix",
                table: "Companies",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "AuditLogs",
                type: "uniqueidentifier",
                nullable: true);

            // ---- Dados: tudo o que existe é da USGA; a Trapiche nasce com a cópia dos motivos ativos. ----
            // USGA = o Id da antiga empresa padrão. Se houver dados sem a empresa cadastrada (não deveria), ela é criada.
            migrationBuilder.Sql($@"
IF NOT EXISTS (SELECT 1 FROM [Companies] WHERE [Id] = '{UsgaId}')
   AND (EXISTS (SELECT 1 FROM [UserRoles]) OR EXISTS (SELECT 1 FROM [InfractionTypes]) OR EXISTS (SELECT 1 FROM [Employees])
        OR EXISTS (SELECT 1 FROM [EmployeeImportBatches]) OR EXISTS (SELECT 1 FROM [DisciplinaryHistoryImportBatches]) OR EXISTS (SELECT 1 FROM [HistoryImportStates]))
    INSERT INTO [Companies] ([Id], [Name], [IsActive], [IsDeleted], [CreatedAt], [Code], [ManagementUnitPrefix])
    VALUES ('{UsgaId}', N'Usina Serra Grande S.A.', 1, 0, SYSDATETIMEOFFSET(), N'', N'');

UPDATE [Companies] SET [Code] = N'USGA', [ManagementUnitPrefix] = N'USGA' WHERE [Id] = '{UsgaId}';
-- Outras empresas (não deveriam existir): código técnico a partir do Id.
UPDATE [Companies] SET [Code] = LEFT(REPLACE(CONVERT(nvarchar(36), [Id]), '-', ''), 10) WHERE [Code] = N'';
UPDATE [Companies] SET [ManagementUnitPrefix] = [Code] WHERE [ManagementUnitPrefix] = N'';

IF NOT EXISTS (SELECT 1 FROM [Companies] WHERE [Id] = '{TrapicheId}' OR [Code] = N'TRAP')
    INSERT INTO [Companies] ([Id], [Name], [IsActive], [IsDeleted], [CreatedAt], [Code], [ManagementUnitPrefix])
    VALUES ('{TrapicheId}', N'Trapiche', 1, 0, SYSDATETIMEOFFSET(), N'TRAP', N'TRAP');

UPDATE [UserRoles] SET [CompanyId] = '{UsgaId}';
UPDATE [Users] SET [CreatedInCompanyId] = '{UsgaId}';
UPDATE [InfractionTypes] SET [CompanyId] = '{UsgaId}';
UPDATE [HistoryImportStates] SET [CompanyId] = '{UsgaId}';
UPDATE [EmployeeImportBatches] SET [CompanyId] = '{UsgaId}';
UPDATE [DisciplinaryHistoryImportBatches] SET [CompanyId] = '{UsgaId}';
UPDATE [AuditLogs] SET [CompanyId] = '{UsgaId}';

-- Motivos da Trapiche: cópia dos motivos ATIVOS da USGA (o catálogo continua editável em cada empresa).
INSERT INTO [InfractionTypes] ([Id], [CompanyId], [Code], [Name], [Description], [DefaultSeverity], [IsActive],
    [RequiresFormalInvestigation], [AllowsTerminationRecommendation], [LegalReference], [CreatedAt], [IsDeleted])
SELECT NEWID(), '{TrapicheId}', u.[Code], u.[Name], u.[Description], u.[DefaultSeverity], 1,
    u.[RequiresFormalInvestigation], u.[AllowsTerminationRecommendation], u.[LegalReference], SYSDATETIMEOFFSET(), 0
FROM [InfractionTypes] u
WHERE u.[CompanyId] = '{UsgaId}' AND u.[IsActive] = 1 AND u.[IsDeleted] = 0
  AND NOT EXISTS (SELECT 1 FROM [InfractionTypes] t WHERE t.[CompanyId] = '{TrapicheId}' AND t.[Code] = u.[Code] AND t.[IsDeleted] = 0);
");

            migrationBuilder.CreateIndex(
                name: "IX_Users_CreatedInCompanyId",
                table: "Users",
                column: "CreatedInCompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_CompanyId_RoleId",
                table: "UserRoles",
                columns: new[] { "CompanyId", "RoleId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_UserId_RoleId_CompanyId",
                table: "UserRoles",
                columns: new[] { "UserId", "RoleId", "CompanyId" },
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_ManagementUnits_CompanyId_Code",
                table: "ManagementUnits",
                columns: new[] { "CompanyId", "Code" },
                unique: true,
                filter: "[Code] IS NOT NULL AND [IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_InfractionTypes_CompanyId_Code",
                table: "InfractionTypes",
                columns: new[] { "CompanyId", "Code" },
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_HistoryImportStates_CompanyId",
                table: "HistoryImportStates",
                column: "CompanyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employees_CompanyId_Cpf",
                table: "Employees",
                columns: new[] { "CompanyId", "Cpf" },
                unique: true,
                filter: "[IsDeleted] = 0 AND [Status] <> 5");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeImportBatches_CompanyId_PreviewedAt",
                table: "EmployeeImportBatches",
                columns: new[] { "CompanyId", "PreviewedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DisciplinaryHistoryImportBatches_CompanyId_PreviewedAt",
                table: "DisciplinaryHistoryImportBatches",
                columns: new[] { "CompanyId", "PreviewedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DisciplinaryCases_CompanyId_HistoryKey",
                table: "DisciplinaryCases",
                columns: new[] { "CompanyId", "HistoryKey" },
                unique: true,
                filter: "[HistoryKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Companies_Code",
                table: "Companies",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CompanyId_CreatedAt",
                table: "AuditLogs",
                columns: new[] { "CompanyId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_DisciplinaryHistoryImportBatches_Companies_CompanyId",
                table: "DisciplinaryHistoryImportBatches",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EmployeeImportBatches_Companies_CompanyId",
                table: "EmployeeImportBatches",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_HistoryImportStates_Companies_CompanyId",
                table: "HistoryImportStates",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InfractionTypes_Companies_CompanyId",
                table: "InfractionTypes",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_UserRoles_Companies_CompanyId",
                table: "UserRoles",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Companies_CreatedInCompanyId",
                table: "Users",
                column: "CreatedInCompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Voltar para empresa única só é possível sem dados de outra empresa (nada é apagado em silêncio).
            migrationBuilder.Sql($@"
IF EXISTS (SELECT 1 FROM [Employees] WHERE [CompanyId] <> '{UsgaId}')
   OR EXISTS (SELECT 1 FROM [UserRoles] WHERE [CompanyId] <> '{UsgaId}')
   OR EXISTS (SELECT 1 FROM [DisciplinaryCases] WHERE [CompanyId] <> '{UsgaId}')
   OR EXISTS (SELECT 1 FROM [ManagementUnits] WHERE [CompanyId] <> '{UsgaId}')
    THROW 50001, N'Há dados de outra empresa além da USGA: a volta para empresa única não é possível sem perda de dados.', 1;
DELETE FROM [InfractionTypes] WHERE [CompanyId] <> '{UsgaId}';
DELETE FROM [HistoryImportStates] WHERE [CompanyId] <> '{UsgaId}';
DELETE FROM [EmployeeImportBatches] WHERE [CompanyId] <> '{UsgaId}';
DELETE FROM [DisciplinaryHistoryImportBatches] WHERE [CompanyId] <> '{UsgaId}';
");

            migrationBuilder.DropForeignKey(
                name: "FK_DisciplinaryHistoryImportBatches_Companies_CompanyId",
                table: "DisciplinaryHistoryImportBatches");

            migrationBuilder.DropForeignKey(
                name: "FK_EmployeeImportBatches_Companies_CompanyId",
                table: "EmployeeImportBatches");

            migrationBuilder.DropForeignKey(
                name: "FK_HistoryImportStates_Companies_CompanyId",
                table: "HistoryImportStates");

            migrationBuilder.DropForeignKey(
                name: "FK_InfractionTypes_Companies_CompanyId",
                table: "InfractionTypes");

            migrationBuilder.DropForeignKey(
                name: "FK_UserRoles_Companies_CompanyId",
                table: "UserRoles");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_Companies_CreatedInCompanyId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_CreatedInCompanyId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_UserRoles_CompanyId_RoleId",
                table: "UserRoles");

            migrationBuilder.DropIndex(
                name: "IX_UserRoles_UserId_RoleId_CompanyId",
                table: "UserRoles");

            migrationBuilder.DropIndex(
                name: "IX_ManagementUnits_CompanyId_Code",
                table: "ManagementUnits");

            migrationBuilder.DropIndex(
                name: "UX_InfractionTypes_CompanyId_Code",
                table: "InfractionTypes");

            migrationBuilder.DropIndex(
                name: "UX_HistoryImportStates_CompanyId",
                table: "HistoryImportStates");

            migrationBuilder.DropIndex(
                name: "IX_Employees_CompanyId_Cpf",
                table: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_EmployeeImportBatches_CompanyId_PreviewedAt",
                table: "EmployeeImportBatches");

            migrationBuilder.DropIndex(
                name: "IX_DisciplinaryHistoryImportBatches_CompanyId_PreviewedAt",
                table: "DisciplinaryHistoryImportBatches");

            migrationBuilder.DropIndex(
                name: "IX_DisciplinaryCases_CompanyId_HistoryKey",
                table: "DisciplinaryCases");

            migrationBuilder.DropIndex(
                name: "UX_Companies_Code",
                table: "Companies");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_CompanyId_CreatedAt",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "CreatedInCompanyId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "UserRoles");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "InfractionTypes");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "HistoryImportStates");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "EmployeeImportBatches");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "DisciplinaryHistoryImportBatches");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "ManagementUnitPrefix",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "AuditLogs");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_UserId_RoleId",
                table: "UserRoles",
                columns: new[] { "UserId", "RoleId" },
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_ManagementUnits_CompanyId_Code",
                table: "ManagementUnits",
                columns: new[] { "CompanyId", "Code" });

            migrationBuilder.CreateIndex(
                name: "UX_InfractionTypes_Code",
                table: "InfractionTypes",
                column: "Code",
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_Cpf",
                table: "Employees",
                column: "Cpf",
                unique: true,
                filter: "[IsDeleted] = 0 AND [Status] <> 5");

            migrationBuilder.CreateIndex(
                name: "IX_DisciplinaryCases_HistoryKey",
                table: "DisciplinaryCases",
                column: "HistoryKey",
                unique: true,
                filter: "[HistoryKey] IS NOT NULL");
        }
    }
}
