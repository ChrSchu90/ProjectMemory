namespace ProjectMemory.Database.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Adds a durable deletion-intent flag to existing project routing records.
/// </summary>
public partial class ProjectDeletionIntent : Migration
{
    #region Static Fields

    #endregion

    #region Private Fields

    #endregion

    #region Constructors

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "DeletionPending",
            table: "ProjectRoutings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The previous schema cannot represent pending deletion. Block the downgrade while any intent remains.
        migrationBuilder.Sql("""
            CREATE TEMP TABLE CheckDeletionIntents (Pending INTEGER CHECK (Pending = 0));
            INSERT INTO CheckDeletionIntents SELECT COUNT(*) FROM ProjectRoutings WHERE DeletionPending;
            DROP TABLE CheckDeletionIntents;
            """);
        migrationBuilder.DropColumn(name: "DeletionPending", table: "ProjectRoutings");
    }

    #endregion

    #region Private Methods

    #endregion
}
