using Integration.Client;

try
{
    if (args.Length is 1 or 2 && args[0] == "catalog")
    {
        var offset = args.Length == 2 ? int.Parse(args[1]) : 0;
        return await ApiClient.ExecuteAsync("GET", $"products?offset={offset}");
    }
    if (args.Length == 1 && args[0] == "history") return await ApiClient.ExecuteAsync("GET", "sales");
    if (args.Length == 4 && args[0] == "sell")
        return await ApiClient.ExecuteAsync("POST", "sales", new {
            productId = long.Parse(args[1]), quantity = int.Parse(args[2]), reference = args[3]
        });
}
catch (Exception error) when (error is FormatException or OverflowException)
{
    Console.Error.WriteLine("ID, cantidad y offset deben ser números enteros válidos.");
    return 1;
}
Console.WriteLine("""
    Aplicación B — Ventas
    catalog [offset]           Consultar catálogo publicado por Inventario
    sell productoId cantidad referenciaUnica
    history                    Consultar ventas registradas
    """);
return args.Length == 0 ? 0 : 1;
