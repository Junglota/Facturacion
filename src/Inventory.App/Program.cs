using Integration.Client;

try
{
    if (args.Length == 1 && args[0] == "sales") return await ApiClient.ExecuteAsync("GET", "sales");
    if (args.Length is 1 or 2 && args[0] == "list")
    {
        var offset = args.Length == 2 ? int.Parse(args[1]) : 0;
        return await ApiClient.ExecuteAsync("GET", $"products?offset={offset}");
    }
    if (args.Length == 2 && args[0] == "delete")
        return await ApiClient.ExecuteAsync("DELETE", $"products/{long.Parse(args[1])}");
    if (args.Length == 4 && args[0] == "create")
        return await ApiClient.ExecuteAsync("POST", "products", new { name = args[1], priceCents = long.Parse(args[2]), stock = int.Parse(args[3]) });
    if (args.Length == 5 && args[0] == "update")
        return await ApiClient.ExecuteAsync("PUT", $"products/{long.Parse(args[1])}", new { name = args[2], priceCents = long.Parse(args[3]), stock = int.Parse(args[4]) });
}
catch (Exception error) when (error is FormatException or OverflowException)
{
    Console.Error.WriteLine("ID, precio, stock y offset deben ser números enteros válidos.");
    return 1;
}
Console.WriteLine("""
    Aplicación A — Inventario
    list [offset]                        Consultar productos
    create "Nombre" precioCentavos stock  Crear producto
    update id "Nombre" precioCentavos stock
    delete id                            Eliminar producto sin ventas
    sales                                Consultar ventas recibidas
    """);
return args.Length == 0 ? 0 : 1;
