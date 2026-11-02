using System;
using System.Reactive.Linq;
using Avalonia.Controls;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia;

/// <summary>
/// Dialog host for the shared transition editor. Owns the editor instance it is given: closing
/// stops any preview, saves pending cue edits (the editor's Cancel path) and disposes it.
/// </summary>
public partial class TransitionEditorWindow : Window
{
    public TransitionEditorWindow()
    {
        InitializeComponent();
    }

    public TransitionEditorWindow(MixTransitionViewModel editor, string title) : this()
    {
        DataContext = editor;
        Title = title;
        Closed += async (_, _) =>
        {
            try
            {
                await editor.CancelCommand.Execute();
            }
            finally
            {
                editor.Dispose();
            }
        };
    }
}
