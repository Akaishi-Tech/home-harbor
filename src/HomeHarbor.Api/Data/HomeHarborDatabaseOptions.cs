namespace HomeHarbor.Api.Data;

public sealed class HomeHarborDatabaseOptions
{
    public const string SectionName = "HomeHarbor:Database";

    public string ConnectionString { get; set; } =
        "Host=/run/postgresql;Port=5432;Database=homeharbor;Username=homeharbor;Pooling=true";
}
