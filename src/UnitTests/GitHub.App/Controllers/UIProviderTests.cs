﻿using System;
using System.ComponentModel.Composition;
using System.Reactive.Linq;
using System.Windows.Controls;
using GitHub.Controllers;
using GitHub.Models;
using GitHub.Services;
using GitHub.UI;
using NSubstitute;
using Xunit;
using UnitTests;
using GitHub.ViewModels;
using ReactiveUI;
using System.Collections.Generic;
using GitHub.Authentication;
using System.Collections.ObjectModel;
using GitHub.VisualStudio;

public class UIProviderTests : TestBaseClass
{
    [Fact]
    public void ListenToCompletionDoesNotThrowInRelease()
    {
        var provider = Substitutes.GetFullyMockedServiceProvider();

        using (var p = new GitHubServiceProvider(provider))
        {
#if DEBUG
            Assert.ThrowsAny<InvalidOperationException>(() =>
            {
#endif
                p.ListenToCompletionState();
#if DEBUG
            });
#endif
        }
    }
}
