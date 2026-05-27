namespace EntryPointEffects.Api.Services;

public sealed class BillingClient
{
    private readonly HttpClient _httpClient;

    public BillingClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> LoadInvoiceAsync(int teamId)
    {
        return await _httpClient.GetStringAsync($"https://billing.example/invoices/{teamId}");
    }

    public async Task<string[]> LoadInvoicesAsync(IReadOnlyList<int> teamIds)
    {
        return await Task.WhenAll(teamIds.Select(teamId => _httpClient.GetStringAsync($"https://billing.example/invoices/{teamId}")));
    }
}
