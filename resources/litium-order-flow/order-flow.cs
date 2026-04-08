// IL2026/IL3050: AOT and trimming warnings for JSON serialization of anonymous types.
// Not applicable – this is a dev tool, never AOT-compiled or trimmed.
#pragma warning disable IL2026, IL3050
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// ============================================================
// Config – edit these values
// ============================================================
var username = "apiuser";
var password = "apiuser";
var host = "https://demo.localtest.me:5001";
var orderId = "LS75757";
// ============================================================

var token = "";
var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
};
var client = new HttpClient(handler);
JsonNode? order = null;
var shipmentId = "";
var rmaSystemId = ""; // active RMA UUID
var sroId = "";       // active SRO ID
var rmaList = new List<JsonNode>();  // all RMAs for current order
var sroList = new List<string>();    // all SRO IDs for current order

Console.WriteLine("=== Order Flow Tool ===");
Console.WriteLine($"Host:    {host}");
Console.WriteLine($"OrderId: {orderId}");
Console.WriteLine();

// Auto-login and fetch order on startup
try
{
    await Login();
    await GetState();
}
catch (Exception ex)
{
    Console.WriteLine($"[ERROR] Could not log in or fetch order: {ex.Message}");
    Console.WriteLine("Check the Config section and verify the server is running.");
}

bool running = true;
while (running)
{
    Console.WriteLine();
    // Session state summary
    if (!string.IsNullOrEmpty(token))
        Console.WriteLine($"Token:   {token[..30]}...");
    if (order is not null)
    {
        Console.WriteLine($"Order:   {order["id"]}  |  {order["customerInfo"]?["firstName"]} {order["customerInfo"]?["lastName"]} ({order["customerInfo"]?["email"]})  |  {order["grandTotal"]} {order["currencyCode"]}");
    }
    Console.WriteLine();
    Console.WriteLine("Choose action:");
    Console.WriteLine("  p.  Print full order JSON");
    Console.WriteLine("  1.  Notify exported");
    Console.WriteLine("  2.  Create shipment – all rows");
    Console.WriteLine("  3.  Create shipment – one row");
    Console.WriteLine("  4.  Mark delivered (Ship)");
    Console.WriteLine("  5.  Create RMA – all rows");
    Console.WriteLine("  6.  Create RMA – interactive");
    Console.WriteLine("  r.  RMA menu");
    Console.WriteLine("  s.  SRO menu");
    Console.WriteLine("  0.  Exit");
    Console.Write("> ");

    var choice = Console.ReadLine()?.Trim();
    Console.WriteLine();

    try
    {
        switch (choice)
        {
            case "p":
            case "P":
                PrintOrder();
                break;
            case "1":
                Console.WriteLine(await NotifyExported());
                break;
            case "2":
                shipmentId = (await CreateShipmentFromAllItems()).Replace("\"", "");
                Console.WriteLine($"ShipmentId: {shipmentId}");
                break;
            case "3":
                shipmentId = (await CreateShipmentFromOneItem()).Replace("\"", "");
                Console.WriteLine($"ShipmentId: {shipmentId}");
                break;
            case "4":
                if (string.IsNullOrEmpty(shipmentId)) { Console.WriteLine("[ERROR] Create a shipment first (option 2 or 3)."); break; }
                await Ship(shipmentId);
                break;
            case "5":
                await CreateRMA(interactive: false);
                break;
            case "6":
                await CreateRMA(interactive: true);
                break;
            case "r":
            case "R":
                await RMAMenu();
                break;
            case "s":
            case "S":
                await SROMenu();
                break;
            case "0":
                running = false;
                break;
            default:
                Console.WriteLine("Invalid choice.");
                break;
        }
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"[HTTP ERROR] {ex.Message}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] {ex.Message}");
    }
}

// ============================================================
// Helper functions
// ============================================================

async Task<string> ReadAndValidate(HttpResponseMessage response)
{
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
        throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    return body;
}

async Task Login()
{
    var form = new StringContent(
        $"grant_type=client_credentials&client_id={Uri.EscapeDataString(username)}&client_secret={Uri.EscapeDataString(password)}",
        Encoding.UTF8,
        "application/x-www-form-urlencoded");

    var response = await client.PostAsync($"{host}/Litium/Oauth/Token", form);
    var body = await ReadAndValidate(response);

    var json = JsonNode.Parse(body) ?? throw new InvalidOperationException("Empty login response.");
    token = json["access_token"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing access_token.");
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    Console.WriteLine("Logged in.");
}

async Task GetOrder()
{
    var response = await client.GetAsync($"{host}/Litium/api/connect/erp/orders/{orderId}");
    var body = await ReadAndValidate(response);
    order = JsonNode.Parse(body) ?? throw new InvalidOperationException("Empty order response.");
}

async Task GetState()
{
    await GetOrder();

    // Collect RMAs
    rmaList.Clear();
    foreach (var r in order!["rmas"]?.AsArray() ?? [])
        if (r is not null) rmaList.Add(r);

    // Collect SRO IDs from salesReturnOrderIds and rmas[].returnSlipId
    sroList.Clear();
    foreach (var id in order["salesReturnOrderIds"]?.AsArray() ?? [])
    {
        var s = id?.GetValue<string>();
        if (!string.IsNullOrEmpty(s) && !sroList.Contains(s)) sroList.Add(s);
    }
    foreach (var r in rmaList)
    {
        var s = r["returnSlipId"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(s) && !sroList.Contains(s)) sroList.Add(s);
    }
}

void PrintOrder()
{
    if (order is null) { Console.WriteLine("[ERROR] No order loaded."); return; }
    Console.WriteLine(order.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}

async Task<string> NotifyExported()
{
    var response = await client.PostAsync(
        $"{host}/Litium/api/connect/erp/orders/{orderId}/notify/exported",
        new StringContent("", Encoding.UTF8, "application/json"));
    return await ReadAndValidate(response);
}

async Task<string> CreateShipmentFromAllItems()
{
    var shipment = new
    {
        Id = $"{orderId}_S1",
        OrderId = orderId,
        ShippingMethod = order!["shippingInfo"]![0]!["shippingMethod"]!.GetValue<string>(),
        rows = order["rows"]!.AsArray()
            .Where(row => row?["type"]?.GetValue<string>() == "product")
            .Select((row, idx) => new
            {
                id = $"{orderId}_S1_{idx}",
                articleNumber = row!["articleNumber"]!.GetValue<string>(),
                quantity = row["quantity"]!.GetValue<decimal>()
            }).ToArray()
    };

    var response = await client.PostAsync(
        $"{host}/Litium/api/connect/erp/orders/{orderId}/shipments",
        JsonContent.Create(shipment));
    return await ReadAndValidate(response);
}

async Task<string> CreateShipmentFromOneItem()
{
    var shipment = new
    {
        Id = $"{orderId}_S1",
        OrderId = orderId,
        ShippingMethod = order!["shippingInfo"]![0]!["shippingMethod"]!.GetValue<string>(),
        rows = order["rows"]!.AsArray()
            .Where(row => row?["type"]?.GetValue<string>() == "product")
            .Take(1)
            .Select((row, idx) => new
            {
                id = $"{orderId}_S1_{idx}",
                articleNumber = row!["articleNumber"]!.GetValue<string>(),
                quantity = row["quantity"]!.GetValue<decimal>()
            }).ToArray()
    };

    var response = await client.PostAsync(
        $"{host}/Litium/api/connect/erp/orders/{orderId}/shipments",
        JsonContent.Create(shipment));
    return await ReadAndValidate(response);
}

async Task Ship(string shipmentId)
{
    Console.WriteLine($"Shipping: {shipmentId}");
    var response = await client.PostAsync(
        $"{host}/Litium/api/connect/erp/orders/{orderId}/shipments/{shipmentId}/notify/delivered",
        new StringContent("", Encoding.UTF8, "application/json"));
    var body = await ReadAndValidate(response);
    var responseJson = JsonNode.Parse(body);
    var shipment = responseJson?["shipments"]?.AsArray().FirstOrDefault(s => s?["id"]?.GetValue<string>() == shipmentId);
    if (shipment is not null)
    {
        Console.WriteLine($"Delivered: {shipment["id"]}");
        Console.WriteLine($"Rows:");
        foreach (var row in shipment["rows"]?.AsArray() ?? [])
            Console.WriteLine($"  {row?["articleNumber"]} x{row?["quantity"]}");
    }
    else
    {
        Console.WriteLine("Shipped. (no shipment details in response)");
    }
}

async Task CreateRMA(bool interactive)
{
    var availableRows = order!["rows"]!.AsArray()
        .Where(row => row?["type"]?.GetValue<string>() == "product")
        .ToList();

    object[] rmaRows;
    if (!interactive)
    {
        rmaRows = availableRows.Select(row => (object)new
        {
            ReturnReason = "RightToReturn",
            ArticleNumber = row!["articleNumber"]!.GetValue<string>(),
            QuantityReturned = row["quantity"]!.GetValue<decimal>()
        }).ToArray();
    }
    else
    {
        Console.WriteLine("Available article numbers:");
        foreach (var row in availableRows)
            Console.WriteLine($"  {row!["articleNumber"]}  (qty: {row["quantity"]})");
        Console.WriteLine();
        Console.WriteLine("Enter article numbers to return, one per line.");
        Console.WriteLine("Format: <articleNumber> <quantity>  (quantity is optional, defaults to full amount)");
        Console.WriteLine("Empty line when done.");
        Console.WriteLine();

        var selectedRows = new List<object>();
        while (true)
        {
            Console.Write("  Article: ");
            var line = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(line)) break;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var articleNumber = parts[0];
            var matchingRow = availableRows.FirstOrDefault(row => row!["articleNumber"]?.GetValue<string>() == articleNumber);
            if (matchingRow is null)
            {
                Console.WriteLine($"  [WARN] Article {articleNumber} not found in order, skipping.");
                continue;
            }

            var maxQty = matchingRow["quantity"]!.GetValue<decimal>();
            decimal qty = maxQty;
            if (parts.Length > 1 && decimal.TryParse(parts[1], out var parsedQty))
                qty = Math.Min(parsedQty, maxQty);

            selectedRows.Add(new { ReturnReason = "RightToReturn", ArticleNumber = articleNumber, QuantityReturned = qty });
            Console.WriteLine($"  Added: {articleNumber} x{qty}");
        }

        if (selectedRows.Count == 0) { Console.WriteLine("No rows entered, RMA cancelled."); return; }
        rmaRows = selectedRows.ToArray();
    }

    var rma = new { SystemId = Guid.NewGuid(), SalesOrderId = orderId, FirstName = "Test", LastName = "User", Phone = "0700000000", Email = "testuser@example.com", Comments = "Test return", PackageCondition = "Opened", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Rows = rmaRows };
    var rmaResponse = await client.PostAsync($"{host}/Litium/api/connect/erp/rmas", JsonContent.Create(rma));
    var rmaBody = await ReadAndValidate(rmaResponse);
    rmaSystemId = JsonNode.Parse(rmaBody)!["id"]!.GetValue<string>();
    var patch = new[] { new { path = "id", op = "replace", value = $"{orderId}_R1" } };
    var patchResponse = await client.PatchAsync($"{host}/Litium/api/admin/sales/returnAuthorizations/{rmaSystemId}", JsonContent.Create(patch));
    var patchBody = await ReadAndValidate(patchResponse);
    var patchJson = JsonNode.Parse(patchBody);
    Console.WriteLine($"RMA created: {patchJson?["id"]}");
    Console.WriteLine("Rows:");
    foreach (var row in patchJson?["rows"]?.AsArray() ?? [])
        Console.WriteLine($"  {row?["articleNumber"]} x{row?["quantityReturned"]}");
}

async Task RMAMenu()
{
    while (true)
    {
        await GetState();
        Console.WriteLine();
        Console.WriteLine("=== RMA Menu ===");
        if (rmaList.Count == 0)
        {
            Console.WriteLine("  No RMAs found for this order.");
            Console.Write("  Press Enter to go back...");
            Console.ReadLine();
            return;
        }
        Console.WriteLine("  Available RMAs:");
        for (int i = 0; i < rmaList.Count; i++)
            Console.WriteLine($"  {i + 1}.  {rmaList[i]["id"]}  state: {rmaList[i]["state"]}");
        Console.WriteLine("  0.  Back");
        Console.Write("> ");
        var sel = Console.ReadLine()?.Trim();
        if (sel == "0") return;
        if (!int.TryParse(sel, out var idx) || idx < 1 || idx > rmaList.Count)
        {
            Console.WriteLine("Invalid choice.");
            continue;
        }
        rmaSystemId = rmaList[idx - 1]["id"]!.GetValue<string>();
        await RMAActionMenu();
    }
}

async Task RMAActionMenu()
{
    while (true)
    {
        await GetState();
        var rmaNode = rmaList.FirstOrDefault(r => r["id"]?.GetValue<string>() == rmaSystemId);
        Console.WriteLine();
        Console.WriteLine("=== RMA Actions ===");
        Console.WriteLine($"  RMA:   {rmaSystemId}");
        Console.WriteLine($"  State: {rmaNode?["state"]}");
        Console.WriteLine();
        Console.WriteLine("  p.  Print full RMA JSON");
        Console.WriteLine("  1.  Notify package received      – RMA: package arrived at warehouse (→ PackageReceived)");
        Console.WriteLine("  2.  Register received quantities  – RMA: set physically received qty (required before approve)");
        Console.WriteLine("  3.  Notify processing             – RMA: notify that returned goods are being processed (→ Processing)");
        Console.WriteLine("  4.  Approve                       – RMA: approve return → Litium auto-creates SRO (→ Approved)");
        Console.WriteLine("  5.  Notify completed              – RMA: notify that physical processing is complete (→ Completed)");
        Console.WriteLine("  0.  Back");
        Console.Write("> ");
        try
        {
            switch (Console.ReadLine()?.Trim())
            {
                case "p":
                case "P":
                    if (rmaNode is null) { Console.WriteLine("[ERROR] RMA not found."); break; }
                    Console.WriteLine(rmaNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    break;
                case "1": await RmaNotify("packageReceived"); break;
                case "2": await RegisterReceivedQuantities(); break;
                case "3": await RmaNotify("processing"); break;
                case "4": await RmaAction("approve"); break;
                case "5": await RmaNotify("completed"); break;
                case "0": return;
                default: Console.WriteLine("Invalid choice."); break;
            }
        }
        catch (HttpRequestException ex) { Console.WriteLine($"[HTTP ERROR] {ex.Message}"); }
        catch (Exception ex) { Console.WriteLine($"[ERROR] {ex.Message}"); }
    }
}

async Task SROMenu()
{
    while (true)
    {
        await GetState();
        Console.WriteLine();
        Console.WriteLine("=== SRO Menu ===");
        if (sroList.Count == 0)
        {
            Console.WriteLine("  No SROs found automatically.");
            Console.Write("  Enter SRO ID manually (or Enter to go back): ");
            var manual = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(manual)) return;
            sroId = manual;
            await SROActionMenu();
            continue;
        }
        Console.WriteLine("  Available SROs:");
        for (int i = 0; i < sroList.Count; i++)
            Console.WriteLine($"  {i + 1}.  {sroList[i]}");
        Console.WriteLine("  0.  Back");
        Console.Write("> ");
        var sel = Console.ReadLine()?.Trim();
        if (sel == "0") return;
        if (!int.TryParse(sel, out var idx) || idx < 1 || idx > sroList.Count)
        {
            Console.WriteLine("Invalid choice.");
            continue;
        }
        sroId = sroList[idx - 1];
        await SROActionMenu();
    }
}

async Task SROActionMenu()
{
    while (true)
    {
        Console.WriteLine();
        Console.WriteLine("=== SRO Actions ===");
        Console.WriteLine($"  SRO: {sroId}");
        Console.WriteLine();
        Console.WriteLine("  1.  Confirm return");
        Console.WriteLine("  2.  Refund");
        Console.WriteLine("  0.  Back");
        Console.Write("> ");
        try
        {
            switch (Console.ReadLine()?.Trim())
            {
                case "1": await SroAction("confirmReturn"); break;
                case "2": await SroAction("refund"); break;
                case "0": return;
                default: Console.WriteLine("Invalid choice."); break;
            }
        }
        catch (HttpRequestException ex) { Console.WriteLine($"[HTTP ERROR] {ex.Message}"); }
        catch (Exception ex) { Console.WriteLine($"[ERROR] {ex.Message}"); }
    }
}

async Task RegisterReceivedQuantities()
{
    // GET current state of the return authorization
    var getResponse = await client.GetAsync($"{host}/Litium/api/admin/sales/returnAuthorizations/{rmaSystemId}");
    var getBody = await ReadAndValidate(getResponse);
    var ra = JsonNode.Parse(getBody)!;

    // Prompt for quantityReceived per row (default = quantityReturned)
    foreach (var row in ra["rows"]?.AsArray() ?? [])
    {
        if (row is null) continue;
        var max = row["quantityReturned"]!.GetValue<decimal>();
        Console.Write($"  {row["articleNumber"]}  returned: {max}  received [Enter={max}]: ");
        var input = Console.ReadLine()?.Trim();
        var received = (decimal.TryParse(input, out var parsed)) ? Math.Min(parsed, max) : max;
        row["quantityReceived"] = received;
    }

    var putResponse = await client.PutAsJsonAsync(
        $"{host}/Litium/api/admin/sales/returnAuthorizations/{rmaSystemId}",
        ra);
    var putBody = await ReadAndValidate(putResponse);
    var putJson = JsonNode.Parse(putBody);
    Console.WriteLine("Received quantities registered:");
    foreach (var row in putJson?["rows"]?.AsArray() ?? [])
        Console.WriteLine($"  {row?["articleNumber"]}  returned: {row?["quantityReturned"]}  received: {row?["quantityReceived"]}");
}

async Task RmaNotify(string action)
{
    var url = $"{host}/Litium/api/connect/erp/rmas/{rmaSystemId}/notify/{action}";
    var response = await client.PostAsync(url, new StringContent("", Encoding.UTF8, "application/json"));
    PrintRmaResponse(await ReadAndValidate(response));
}

async Task RmaAction(string action)
{
    var url = $"{host}/Litium/api/connect/erp/rmas/{rmaSystemId}/action/{action}";
    var response = await client.PostAsync(url, new StringContent("", Encoding.UTF8, "application/json"));
    var body = await ReadAndValidate(response);
    var json = JsonNode.Parse(body);
    // Capture the SRO ID returned by approve
    var returnSlipId = json?["returnSlipId"]?.GetValue<string>();
    if (!string.IsNullOrEmpty(returnSlipId))
    {
        sroId = returnSlipId;
        Console.WriteLine($"SRO created: {sroId}");
    }
    PrintRmaResponse(body);
}

void PrintRmaResponse(string body)
{
    var json = JsonNode.Parse(body);
    Console.WriteLine($"State: {json?["state"]}");
    Console.WriteLine("Rows:");
    foreach (var row in json?["rows"]?.AsArray() ?? [])
        Console.WriteLine($"  {row?["articleNumber"]}  returned: {row?["quantityReturned"]}  received: {row?["quantityReceived"]}");
}

async Task SroAction(string action)
{
    if (string.IsNullOrEmpty(sroId))
    {
        Console.Write("SRO ID: ");
        sroId = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrEmpty(sroId)) { Console.WriteLine("No ID entered."); return; }
    }
    Console.WriteLine($"SRO action '{action}' on {sroId}");
    var url = $"{host}/Litium/api/connect/erp/salesReturnOrders/{sroId}/action/{action}";
    var response = await client.PostAsync(url, new StringContent("", Encoding.UTF8, "application/json"));
    var body = await ReadAndValidate(response);
    var json = JsonNode.Parse(body);
    Console.WriteLine($"SRO: {json?["id"]}  |  {json?["grandTotal"]} {json?["currencyCode"]}");
    Console.WriteLine("Rows:");
    foreach (var row in json?["rows"]?.AsArray() ?? [])
        Console.WriteLine($"  {row?["articleNumber"]}  qty: {row?["quantity"]}  total: {row?["totalIncludingVat"]}");
    var rmaState = json?["rmas"]?.AsArray().LastOrDefault()?["state"];
    if (rmaState is not null)
        Console.WriteLine($"RMA state: {rmaState}");
}