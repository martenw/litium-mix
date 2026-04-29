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
var sroId = ""; // optional: set to an existing SRO ID to test SRO actions directly
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
    Console.WriteLine("  4.  Mark delivered (Ship) - Wait for payment capture success");
    Console.WriteLine("  5.  Create RMA – all rows");
    Console.WriteLine("  6.  Create RMA – interactive");
    Console.WriteLine("  r.  RMA menu");
    Console.WriteLine("  s.  SRO menu");
    Console.WriteLine("  o.  [Admin API] Try set order state...");
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
            case "o":
            case "O":
                await OrderStateMenu();
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

async Task<string> Send(HttpMethod method, string url, HttpContent? content = null)
{
    Console.WriteLine($"{method} {url}");
    using var request = new HttpRequestMessage(method, url) { Content = content };
    var response = await client.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
        throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    return body;
}

void ConsoleWrite(IEnumerable<string> lines, string prompt = "> ")
{
    foreach (var line in lines)
        Console.WriteLine(line);
    Console.Write(prompt);
}

async Task Login()
{
    var form = new StringContent(
        $"grant_type=client_credentials&client_id={Uri.EscapeDataString(username)}&client_secret={Uri.EscapeDataString(password)}",
        Encoding.UTF8,
        "application/x-www-form-urlencoded");

    var body = await Send(HttpMethod.Post, $"{host}/Litium/Oauth/Token", form);

    var json = JsonNode.Parse(body) ?? throw new InvalidOperationException("Empty login response.");
    token = json["access_token"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing access_token.");
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    Console.WriteLine("Logged in.");
}

async Task GetOrder()
{
    var body = await Send(HttpMethod.Get, $"{host}/Litium/api/connect/erp/orders/{orderId}");
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

    // Pick up shipmentId from existing shipments if not already set in this session
    if (string.IsNullOrEmpty(shipmentId))
    {
        var firstShipment = order["shipments"]?.AsArray().FirstOrDefault();
        if (firstShipment is not null)
            shipmentId = firstShipment["id"]?.GetValue<string>() ?? "";
    }
}

void PrintOrder()
{
    if (order is null) { Console.WriteLine("[ERROR] No order loaded."); return; }
    Console.WriteLine(order.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}

async Task OrderStateMenu()
{
    while (true)
    {
        Console.WriteLine();
        Console.WriteLine("=== Order State Menu ===");
        var states = new[] { 
            "Init", 
            "Confirmed", 
            "PendingProcessing", 
            "Processing", 
            "Completed", 
            "Cancelled" 
        };
        for (int i = 0; i < states.Length; i++)
        {
            Console.WriteLine($"  {i + 1}.  {states[i]}");
        }
        Console.WriteLine("  0.  Back");
        Console.Write("> ");
        
        var sel = Console.ReadLine()?.Trim();
        if (sel == "0") return;
        
        if (int.TryParse(sel, out var idx) && idx >= 1 && idx <= states.Length)
        {
            await SetSalesOrderState(states[idx - 1]);
            return;
        }
        Console.WriteLine("Invalid choice.");
    }
}

async Task SetSalesOrderState(string targetState)
{
    // Lookup Order system ID (UUID) from string ID
    var lookupBody = await Send(HttpMethod.Post,
        $"{host}/Litium/api/admin/sales/salesOrders/keyLookups",
        JsonContent.Create(new[] { orderId }));
    
    var orderSystemId = JsonNode.Parse(lookupBody)?[orderId]?.GetValue<string>();
    if (string.IsNullOrEmpty(orderSystemId))
    {
        Console.WriteLine($"[ERROR] Could not resolve systemId for Order {orderId}");
        return;
    }

    try
    {
        var url = $"{host}/Litium/api/admin/sales/salesOrders/{orderSystemId}/stateTransition/{targetState}";
        var body = await Send(HttpMethod.Put, url, new StringContent("", Encoding.UTF8, "application/json"));
        Console.WriteLine($"[Admin API] Successfully transitioned order {orderId} to {targetState}.");
        
        // Refresh the local state
        await GetState();
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"[ERROR] Admin API state transition failed: {ex.Message}");
        Console.WriteLine("If this endpoint fails with 404, verify the systemId or target state.");
    }
}

async Task<string> NotifyExported()
{
    return await Send(HttpMethod.Post, $"{host}/Litium/api/connect/erp/orders/{orderId}/notify/exported",
        new StringContent("", Encoding.UTF8, "application/json"));
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

    return await Send(HttpMethod.Post, $"{host}/Litium/api/connect/erp/orders/{orderId}/shipments",
        JsonContent.Create(shipment));
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

    return await Send(HttpMethod.Post, $"{host}/Litium/api/connect/erp/orders/{orderId}/shipments",
        JsonContent.Create(shipment));
}

async Task Ship(string shipmentId)
{
    Console.WriteLine($"Shipping: {shipmentId}");
    var body = await Send(HttpMethod.Post,
        $"{host}/Litium/api/connect/erp/orders/{orderId}/shipments/{shipmentId}/notify/delivered",
        new StringContent("", Encoding.UTF8, "application/json"));
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
    var rmaBody = await Send(HttpMethod.Post, $"{host}/Litium/api/connect/erp/rmas", JsonContent.Create(rma));
    rmaSystemId = JsonNode.Parse(rmaBody)!["id"]!.GetValue<string>();
    var patch = new[] { new { path = "id", op = "replace", value = $"{orderId}_R1" } };
    var patchBody = await Send(HttpMethod.Patch, $"{host}/Litium/api/admin/sales/returnAuthorizations/{rmaSystemId}", JsonContent.Create(patch));
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
            ConsoleWrite(["  No RMAs found for this order."], "  Press Enter to go back...");
            Console.ReadLine();
            return;
        }
        var rmaLines = rmaList.Select((r, i) => $"  {i + 1}.  {r["id"]}  state: {r["state"]}")
            .Prepend("  Available RMAs:").Append("  0.  Back");
        ConsoleWrite(rmaLines);
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
        ConsoleWrite([
            "",
            "=== RMA Actions ===",
            $"  RMA:   {rmaSystemId}",
            $"  State: {rmaNode?["state"]}",
            "",
            "  p.  Print full RMA JSON",
            "  1.  Notify package received       – RMA: package arrived at warehouse (→ PackageReceived)",
            "  2.  [Admin API] Register received quantities  – RMA: set physically received qty (required before approve)",
            "  3.  Notify processing             – RMA: notify that returned goods are being processed (→ Processing)",
            "  4.  Approve                       – RMA: approve return → Litium auto-creates SRO (→ Approved)",
            "  5.  Notify completed              – RMA: notify that physical processing is complete (→ Completed)",
            "  0.  Back"
        ]);
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
    if (!string.IsNullOrEmpty(sroId))
    {
        await SROActionMenu();
        return;
    }
    while (true)
    {
        await GetState();
        Console.WriteLine();
        Console.WriteLine("=== SRO Menu ===");
        string? selectedId;
        if (sroList.Count == 0)
        {
            ConsoleWrite(["  No SROs found automatically."], "  Enter SRO ID manually (or Enter to go back): ");
            var manual = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(manual)) return;
            selectedId = manual;
        }
        else
        {
            var sroLines = sroList.Select((s, i) => $"  {i + 1}.  {s}")
                .Prepend("  Available SROs:").Append("  0.  Back");
            ConsoleWrite(sroLines);
            var sel = Console.ReadLine()?.Trim();
            if (sel == "0") return;
            if (!int.TryParse(sel, out var idx) || idx < 1 || idx > sroList.Count)
            {
                Console.WriteLine("Invalid choice.");
                continue;
            }
            selectedId = sroList[idx - 1];
        }
        try
        {
            sroId = selectedId;
            await SROActionMenu();
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"[HTTP ERROR] {ex.Message}");
        }
    }
}

async Task SROActionMenu()
{
    while (true)
    {
        var sroBody = await Send(HttpMethod.Get, $"{host}/Litium/api/connect/erp/orders/{sroId}");
        var sroOrder = JsonNode.Parse(sroBody);
        ConsoleWrite([
            "",
            "=== SRO Actions ===",
            $"  SRO: {sroOrder?["id"]}  |  {sroOrder?["grandTotal"]} {sroOrder?["currencyCode"]}",
            "",
            "  p.  Print SRO JSON",
            "  1.  Confirm return",
            "  2.  [Admin API] Add refund fee",
            "  3.  Refund",
            "  0.  Back"
        ]);
        try
        {
            switch (Console.ReadLine()?.Trim())
            {
                case "p":
                case "P":
                    Console.WriteLine(sroOrder?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    break;
                case "1": await SroAction("confirmReturn"); break;
                case "2": await AddRefundFee(sroOrder!); break;
                case "3": await SroAction("refund"); break;
                case "0": return;
                default: Console.WriteLine("Invalid choice."); break;
            }
        }
        catch (HttpRequestException ex) { Console.WriteLine($"[HTTP ERROR] {ex.Message}"); }
        catch (Exception ex) { Console.WriteLine($"[ERROR] {ex.Message}"); }
    }
}

async Task AddRefundFee(JsonNode sroOrder)
{
    Console.Write("  Fee amount (incl. VAT): ");
    if (!decimal.TryParse(Console.ReadLine()?.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var fee) || fee <= 0)
    {
        Console.WriteLine("Invalid amount.");
        return;
    }

    // Collect product rows and group by VAT rate for pro-rata distribution
    var productRows = sroOrder["rows"]?.AsArray()
        .Where(r => r?["type"]?.GetValue<string>() == "product")
        .ToList() ?? [];

    var totalAbsIncl = productRows.Sum(r => Math.Abs(r?["totalIncludingVat"]?.GetValue<decimal>() ?? 0));
    if (totalAbsIncl == 0)
        throw new InvalidOperationException("No product rows with amounts in SRO.");

    var vatGroups = productRows
        .GroupBy(r => r?["vatRate"]?.GetValue<decimal>() ?? 0m)
        .Select(g => (VatRate: g.Key, AbsTotal: g.Sum(r => Math.Abs(r?["totalIncludingVat"]?.GetValue<decimal>() ?? 0))))
        .ToList();

    // Build one fee row per VAT rate; last group absorbs rounding remainder
    var feeRows = new List<object>();
    var allocated = 0m;
    for (int i = 0; i < vatGroups.Count; i++)
    {
        var g = vatGroups[i];
        var portionIncl = i < vatGroups.Count - 1
            ? Math.Round(fee * g.AbsTotal / totalAbsIncl, 2)
            : fee - allocated;
        var vatAmount = Math.Round(portionIncl * g.VatRate / (1 + g.VatRate), 2);
        var portionExcl = portionIncl - vatAmount;
        allocated += portionIncl;
        var pct = Math.Round(g.VatRate * 100);
        Console.WriteLine($"  VAT {pct}%: portion {portionIncl} incl  =  {portionExcl} excl  +  {vatAmount} vat  (based on {g.AbsTotal}/{totalAbsIncl} of product rows)");
        feeRows.Add(new
        {
            systemId = Guid.NewGuid(),
            orderRowType = "fee",
            description = "Refund fee",
            quantity = 1,
            unitPriceIncludingVat = portionIncl,
            unitPriceExcludingVat = portionExcl,
            totalIncludingVat = portionIncl,
            totalExcludingVat = portionExcl,
            totalVat = vatAmount,
            vatRate = g.VatRate,
            vatDetails = new[] { new { vatRate = g.VatRate, amountIncludingVat = portionIncl, vat = vatAmount } }
        });
    }

    // Lookup SRO system ID (UUID) from string ID
    var lookupBody = await Send(HttpMethod.Post,
        $"{host}/Litium/api/admin/sales/salesReturnOrders/keyLookups",
        JsonContent.Create(new[] { sroId }));
    var sroSystemId = JsonNode.Parse(lookupBody)?[sroId]?.GetValue<string>();
    if (string.IsNullOrEmpty(sroSystemId))
        throw new InvalidOperationException($"Could not resolve systemId for SRO {sroId}");

    var patchOps = feeRows.Select(r => new { op = "add", path = "/rows/-", value = r }).ToArray();
    var patchContent = new StringContent(JsonSerializer.Serialize(patchOps), Encoding.UTF8, "application/json-patch+json");
    var patchBody = await Send(HttpMethod.Patch,
        $"{host}/Litium/api/admin/sales/salesReturnOrders/{sroSystemId}",
        patchContent);
    var patchJson = JsonNode.Parse(patchBody);
    Console.WriteLine($"SRO: {patchJson?["id"]}  |  grandTotal: {patchJson?["grandTotal"]} {patchJson?["currencyCode"]}");
    Console.WriteLine("Rows:");
    foreach (var row in patchJson?["rows"]?.AsArray() ?? [])
        Console.WriteLine($"  [{row?["orderRowType"]}] {row?["description"]}  total: {row?["totalIncludingVat"]}  vat: {row?["totalVat"]}");
}

async Task RegisterReceivedQuantities()
{
    // GET current state of the return authorization
    var getBody = await Send(HttpMethod.Get, $"{host}/Litium/api/admin/sales/returnAuthorizations/{rmaSystemId}");
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

    var putBody = await Send(HttpMethod.Put, $"{host}/Litium/api/admin/sales/returnAuthorizations/{rmaSystemId}",
        JsonContent.Create(ra));
    var putJson = JsonNode.Parse(putBody);
    Console.WriteLine("Received quantities registered:");
    foreach (var row in putJson?["rows"]?.AsArray() ?? [])
        Console.WriteLine($"  {row?["articleNumber"]}  returned: {row?["quantityReturned"]}  received: {row?["quantityReceived"]}");
}

async Task RmaNotify(string action)
{
    var url = $"{host}/Litium/api/connect/erp/rmas/{rmaSystemId}/notify/{action}";
    PrintRmaResponse(await Send(HttpMethod.Post, url, new StringContent("", Encoding.UTF8, "application/json")));
}

async Task RmaAction(string action)
{
    var url = $"{host}/Litium/api/connect/erp/rmas/{rmaSystemId}/action/{action}";
    var body = await Send(HttpMethod.Post, url, new StringContent("", Encoding.UTF8, "application/json"));
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
    var body = await Send(HttpMethod.Post, url, new StringContent("", Encoding.UTF8, "application/json"));
    var json = JsonNode.Parse(body);
    Console.WriteLine($"SRO: {json?["id"]}  |  {json?["grandTotal"]} {json?["currencyCode"]}");
    Console.WriteLine("Rows:");
    foreach (var row in json?["rows"]?.AsArray() ?? [])
        Console.WriteLine($"  {row?["articleNumber"]}  qty: {row?["quantity"]}  total: {row?["totalIncludingVat"]}");
    var rmaState = json?["rmas"]?.AsArray().LastOrDefault()?["state"];
    if (rmaState is not null)
        Console.WriteLine($"RMA state: {rmaState}");
}