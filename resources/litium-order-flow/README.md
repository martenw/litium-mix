# Litium Order Flow tool

Manually work with status adjustments and returns on a Litium Order

Run the file order-flow.cs as a [standalone single file program](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/tutorials/file-based-programs) (requires [.NET 10 SDK or later](https://dotnet.microsoft.com/download/dotnet/10.0))

```PowerShell
dotnet order-flow.cs
```

- Edit the variables in the Config section below (username, password, host, orderId)
- username/password should be to a Litium Service account with full permissions

## Typical flow

### Order fulfillment:

- 1: Notify exported – tell Litium the order has been sent to ERP
- 2: Create shipment – register which items are being shipped (all or one row)
- 3: Mark delivered – confirm the shipment has been delivered

### Return (RMA → SRO):

- 5/6. Create RMA – register a return request; Litium creates the RMA object
- r → select RMA:
  - 1: Package received – notify that the returned package has arrived
  - 2: Register received qty – set how many items were physically received (required before approve)
  - 3: Notify processing – notify that returned goods are being processed
  - 4: Approve – approve the return; Litium automatically creates the SRO
  - 5: Notify completed – notify that physical processing is complete
- s → select SRO:
  - 1: Confirm return – confirm the financial return in the SRO
  - 2: Refund – trigger the refund
