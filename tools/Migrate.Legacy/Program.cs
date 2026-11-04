using Migration;
if (args.Length != 3)
{
    Console.Error.WriteLine("Uso: dotnet run --project tools/Migrate.Legacy -- origen.db inventory.db sales.db");
    return 1;
}
try
{
    LegacyMigrator.Migrate(args[0], args[1], args[2]);
    Console.WriteLine("Migración terminada. La base original se conservó sin cambios.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
