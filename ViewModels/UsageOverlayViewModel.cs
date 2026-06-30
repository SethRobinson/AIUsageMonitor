using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.ViewModels;

public sealed class UsageOverlayViewModel : INotifyPropertyChanged
{
    private string _generatedAtText = "Waiting for usage data";
    private string _autoRefreshText = "Auto refresh every 20 minutes";
    private string _logSummaryText = "No recent errors";
    private string _errorMessage = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ProviderUsageCard> Providers { get; } = [];

    public string AppVersionText => AppMetadata.VersionText;

    public string GeneratedAtText
    {
        get => _generatedAtText;
        private set => SetField(ref _generatedAtText, value);
    }

    public string AutoRefreshText
    {
        get => _autoRefreshText;
        private set => SetField(ref _autoRefreshText, value);
    }

    public string LogSummaryText
    {
        get => _logSummaryText;
        private set => SetField(ref _logSummaryText, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetField(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasProviders => Providers.Count > 0;

    public void ApplySnapshot(UsageSnapshot snapshot)
    {
        Providers.Clear();

        foreach (var provider in snapshot.Providers)
        {
            Providers.Add(new ProviderUsageCard(provider));
        }

        OnPropertyChanged(nameof(HasProviders));
        var generatedAt = snapshot.GeneratedAt == default ? DateTimeOffset.Now : snapshot.GeneratedAt;
        GeneratedAtText = $"Updated {generatedAt.ToLocalTime():MMM d, yyyy h:mm tt}";
        ErrorMessage = string.Empty;
    }

    public void ApplyProvider(ProviderUsage provider)
    {
        var card = new ProviderUsageCard(provider);
        var existingIndex = FindProviderIndex(provider.Name);

        if (existingIndex >= 0)
        {
            Providers[existingIndex] = card;
        }
        else
        {
            Providers.Add(card);
            OnPropertyChanged(nameof(HasProviders));
        }

        ErrorMessage = string.Empty;
    }

    public void SetSnapshotMetadata(DateTimeOffset generatedAt)
    {
        GeneratedAtText = $"Updated {generatedAt.ToLocalTime():MMM d, yyyy h:mm tt}";
        ErrorMessage = string.Empty;
    }

    public void SetChecking(IEnumerable<string> providerNames)
    {
        var names = providerNames.ToList();
        if (names.Count == 0)
        {
            Providers.Clear();
            OnPropertyChanged(nameof(HasProviders));
            return;
        }

        if (ProviderListChanged(names))
        {
            Providers.Clear();
            foreach (var name in names)
            {
                var card = new ProviderUsageCard(new ProviderUsage
                {
                    Name = name,
                    Source = "Live/local collectors",
                    StatusMessage = "Checking usage data...",
                    IsUnavailable = true
                });
                card.SetChecking(true);
                Providers.Add(card);
            }

            OnPropertyChanged(nameof(HasProviders));
            return;
        }

        foreach (var provider in Providers)
        {
            provider.SetChecking(true);
        }
    }

    public void ClearChecking()
    {
        foreach (var provider in Providers)
        {
            provider.SetChecking(false);
        }
    }

    private bool ProviderListChanged(IReadOnlyCollection<string> providerNames)
    {
        if (Providers.Count != providerNames.Count)
        {
            return true;
        }

        var existingProviderNames = Providers
            .Select(provider => provider.ShortName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return providerNames.Any(providerName => !existingProviderNames.Contains(providerName));
    }

    private int FindProviderIndex(string providerName)
    {
        for (var index = 0; index < Providers.Count; index++)
        {
            if (string.Equals(Providers[index].ShortName, providerName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    public void RefreshRelativeTimes()
    {
        foreach (var provider in Providers)
        {
            provider.RefreshCheckedText();
        }
    }

    public void SetError(string message)
    {
        ErrorMessage = message;
    }

    public void SetAutoRefreshInterval(int minutes)
    {
        AutoRefreshText = minutes == 1
            ? "Auto refresh every minute"
            : $"Auto refresh every {minutes} minutes";
    }

    public void SetLogSummary(int errorCount)
    {
        LogSummaryText = errorCount == 0
            ? "No recent errors"
            : $"{errorCount} recent error{(errorCount == 1 ? string.Empty : "s")}";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
