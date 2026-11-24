namespace ProjectMemory.Database;

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ProjectMemory.Database.Converters;
using ProjectMemory.Database.Entities;
using ProjectMemory.Routing;

/// <summary>
/// Provides EF Core access to the ProjectMemory lifecycle database.
/// </summary>
public sealed class ProjectMemoryDbContext : DbContext
{
    #region Static Fields

    #endregion

    #region Private Fields

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new ProjectMemory database context.
    /// </summary>
    /// <param name="options">The configured EF Core options.</param>
    public ProjectMemoryDbContext(DbContextOptions<ProjectMemoryDbContext> options)
        : base(options)
    {
    }

    #endregion

    #region Properties

    /// <summary>
    /// Gets the persisted projects that are available for routing.
    /// </summary>
    internal DbSet<ProjectRoutingEntity> RoutingProjects => Set<ProjectRoutingEntity>();

    /// <summary>
    /// Gets the persisted context bindings.
    /// </summary>
    internal DbSet<ContextBindingEntity> ContextBindings => Set<ContextBindingEntity>();

    #endregion

    #region Public Methods

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Configure DateTimeOffset properties to use Unix milliseconds conversion.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToUnixMillisecondsConverter>();

        // Store enums as strings in the database for better readability and maintainability.
        configurationBuilder.Properties<BindingType>().HaveConversion<EnumToStringConverter<BindingType>>();
        configurationBuilder.Properties<Status>().HaveConversion<EnumToStringConverter<Status>>();
        configurationBuilder.Properties<ProjectStatus>().HaveConversion<EnumToStringConverter<ProjectStatus>>();
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ContextBindingEntity>().Property(binding => binding.BindingName).UseCollation("BINARY");
        modelBuilder.Entity<ContextBindingEntity>().ToTable(table =>
        {
            table.HasCheckConstraint("CK_ContextBindings_Name", "length(\"BindingName\") BETWEEN 1 AND 255 AND \"BindingName\" NOT GLOB '*[^!-~]*' AND instr(\"BindingName\", char(0)) = 0");
            table.HasCheckConstraint("CK_ContextBindings_Type", "\"BindingType\" IN ('ChatProject', 'GitRepository', 'LocalRepository')");
            table.HasCheckConstraint("CK_ContextBindings_Status", "\"Status\" IN ('Active', 'Inactive')");
        });

        modelBuilder.Entity<ProjectRoutingEntity>().Property(project => project.DeletionPending).HasDefaultValue(false);
        modelBuilder.Entity<ProjectRoutingEntity>().ToTable(table => table.HasCheckConstraint("CK_ProjectRoutings_Status", "\"Status\" IN ('Active', 'Archived')"));
    }

    #endregion

    #region Private Methods

    #endregion

    #region ContextFactory

    /// <summary>
    /// Creates a database context for EF Core design-time tooling.
    /// </summary>
    [SuppressMessage("ReSharper", "UnusedType.Local")]
    private sealed class ProjectMemoryDbContextFactory : IDesignTimeDbContextFactory<ProjectMemoryDbContext>
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
        public ProjectMemoryDbContext CreateDbContext(string[] args)
        {
            var designTimeRoot = Path.Combine(Path.GetTempPath(), "ProjectMemory.EFDesign", Path.GetRandomFileName());
            Directory.CreateDirectory(designTimeRoot);

            var databasePaths = new DatabasePaths(designTimeRoot);
            var options = ContextOptions.Create(databasePaths.DatabasePath, pooling: false);
            return new ProjectMemoryDbContext(options);
        }

        #endregion

        #region Private Methods

        #endregion
    }

    #endregion
}
