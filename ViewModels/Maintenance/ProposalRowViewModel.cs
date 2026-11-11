using System.ComponentModel;
using System.Linq;
using SLSKDONET.Services;
using SLSKDONET.Services.Library.Maintenance;

namespace SLSKDONET.ViewModels.Maintenance;

/// <summary>A selectable review row wrapping one <see cref="ProposedTagChange"/>.</summary>
public sealed class ProposalRowViewModel : INotifyPropertyChanged
{
    public ProposalRowViewModel(ProposedTagChange proposal)
    {
        Proposal = proposal;
        _isSelected = proposal.DefaultSelected;
    }

    public ProposedTagChange Proposal { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string Rule => Proposal.Rule;
    public string Evidence => Proposal.Evidence;
    public bool IsHigh => Proposal.Confidence == ProposalConfidence.High;
    public bool IsMedium => Proposal.Confidence == ProposalConfidence.Medium;
    public string Fields => string.Join(", ", Proposal.Changes.Select(c => c.Field.ToString()));
    public string ChangeText => string.Join("  →  ", Proposal.Changes.Select(c => $"{c.Field}: {c.Value ?? "clear"}"));

    public event PropertyChangedEventHandler? PropertyChanged;
}
