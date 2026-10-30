// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using Windows.System;
using Windows.UI.Core;

namespace UwpMigration.Mechanical.Tests;

internal sealed class VanArsdelKeyboardCompiledFixture : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool IsControlDown()
    {
        _ = PropertyChanged;
        return CoreWindow.GetForCurrentThread()
            .GetKeyState(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
    }

    internal void RaisePropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
}
