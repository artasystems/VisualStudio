using System;
using System.ComponentModel.Composition;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using GitHub.Api;
using GitHub.App.Factories;
using GitHub.Exports;
using GitHub.Extensions;
using GitHub.Models;
using GitHub.Services;
using GitHub.UI;
using GitHub.ViewModels;
using GitHub.VisualStudio.Base;
using GitHub.VisualStudio.Helpers;
using NullGuard;
using ReactiveUI;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Threading.Tasks;
using GitHub.VisualStudio.UI;
using System.Windows.Threading;
using GitHub.Helpers;
using GitHub.VisualStudio.UI.Controls;
using System.Reactive.Subjects;
using GitHub.Primitives;

namespace GitHub.VisualStudio.UI.Views
{
    [NullGuard(ValidationFlags.None)]
    class NavigationController : NotificationAwareObject, IDisposable
    {
        readonly List<IUIController> history = new List<IUIController>();
        readonly Dictionary<UIControllerFlow, IUIController> controllers = new Dictionary<UIControllerFlow, IUIController>();
        readonly IUIProvider uiProvider;

        int current = -1;

        public bool HasBack => current > 0;
        public bool HasForward => current < history.Count - 1;
        public IUIController Current => current >= 0 ? history[current] : null;
        int Pointer
        {
            get
            {
                return current;
            }
            set
            {
                if (current == value)
                    return;

                bool raiseBack = false, raiseForward = false;
                if ((value == 0 && HasBack) || (value > 0 && !HasBack))
                    raiseBack = true;
                if ((value == history.Count - 1 && !HasForward) || (value < history.Count - 1 && HasForward))
                    raiseForward = true;
                current = value;
                this.RaisePropertyChanged("Current");
                if (raiseBack) this.RaisePropertyChanged("HasBack");
                if (raiseForward) this.RaisePropertyChanged("HasForward");
            }
        }

        public NavigationController(IUIProvider uiProvider)
        {
            this.uiProvider = uiProvider;
        }

        public void LoadView(IConnection connection, ViewWithData data, Action<IView> onViewLoad)
        {
            switch (data.MainFlow)
            {
                case UIControllerFlow.PullRequestCreation:
                    if (data.MainFlow == Current.SelectedFlow)
                    {
                        Pop();
                    }
                    CreateView(connection, data, onViewLoad);
                break;

                case UIControllerFlow.PullRequestDetail:
                    CreateView(connection, data, onViewLoad);
                break;

                case UIControllerFlow.PullRequestList:
                case UIControllerFlow.Home:
                default:
                    if (data.MainFlow == Current.SelectedFlow)
                    {
                        Reload();
                    }
                    else
                    {
                        ReuseView(connection, data, onViewLoad);
                    }
                break;
            }
        }

        void CreateView(IConnection connection, ViewWithData data, Action<IView> onViewLoad)
        {
            var controller = CreateController(connection, data, onViewLoad);
            Push(controller);
        }

        void ReuseView(IConnection connection, ViewWithData data, Action<IView> onViewLoad)
        {
            IUIController controller;
            var exists = controllers.TryGetValue(data.MainFlow, out controller);

            if (!exists)
            {
                controller = CreateController(connection, data, onViewLoad);
            }

            Push(controller);

            if (exists)
            {
                Reload();
            }
        }

        public void Reload()
        {
            Current?.Reload();
        }

        public void Back()
        {
            if (!HasBack)
                return;
            Pointer--;
            Reload();
        }

        public void Forward()
        {
            if (!HasForward)
                return;
            Pointer++;
            Reload();
        }

        IUIController CreateController(IConnection connection, ViewWithData data, Action<IView> onViewLoad)
        {
            var controller = uiProvider.Configure(data.MainFlow, connection, data);
            controller.TransitionSignal.Subscribe(
                loadData => onViewLoad(loadData.View),
                () => {
                    Pop();
                    Reload();
                });
            controllers.Add(data.MainFlow, controller);
            controller.Start();
            return controller;
        }

        void Push(IUIController controller)
        {
            history.Add(controller);
            Pointer++;
        }

        void Pop()
        {
            var controller = history[history.Count - 1];
            history.RemoveAt(history.Count - 1);
            current--;
            for (int i = 0; i < history.Count; i++)
            {
                if (history[i] == controller)
                {
                    history.RemoveAt(i);
                    current--;
                    i--;
                }
            }
            Pointer = current;
        }

        bool disposed = false;
        protected void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!disposed)
                {
                    disposed = true;
                    controllers.Values.ForEach(c => uiProvider.StopUI(c));
                    controllers.Clear();
                    history.Clear();
                }
            }
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

    [ExportViewModel(ViewType = UIViewType.GitHubPane)]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    [NullGuard(ValidationFlags.None)]
    public class GitHubPaneViewModel : TeamExplorerItemBase, IGitHubPaneViewModel
    {
        const UIControllerFlow DefaultControllerFlow = UIControllerFlow.PullRequestList;

        bool initialized;
        readonly CompositeDisposable disposables = new CompositeDisposable();

        readonly IRepositoryHosts hosts;
        readonly SynchronizationContext syncContext;
        readonly IConnectionManager connectionManager;
        readonly IUIProvider uiProvider;
        NavigationController navController;

        bool disabled;
        Microsoft.VisualStudio.Shell.OleMenuCommand back, forward, refresh;
        int latestReloadCallId;

        [ImportingConstructor]
        public GitHubPaneViewModel(ISimpleApiClientFactory apiFactory, ITeamExplorerServiceHolder holder,
            IConnectionManager cm, IRepositoryHosts hosts, INotificationDispatcher notifications,
            IUIProvider uiProvider)
            : base(apiFactory, holder)
        {
            this.connectionManager = cm;
            this.hosts = hosts;
            this.uiProvider = uiProvider;

            syncContext = SynchronizationContext.Current;
            CancelCommand = ReactiveCommand.Create();
            Title = "GitHub";
            Message = String.Empty;

            this.WhenAnyValue(x => x.Control.DataContext)
                .OfType<BaseViewModel>()
                .Select(x => x.WhenAnyValue(y => y.Title))
                .Switch()
                .Subscribe(x => Title = x ?? "GitHub");
        }

        public override void Initialize(IServiceProvider serviceProvider)
        {
            serviceProvider.AddCommandHandler(GuidList.guidGitHubToolbarCmdSet, PkgCmdIDList.pullRequestCommand,
                (s, e) => Load(new ViewWithData(UIControllerFlow.PullRequestList)).Forget());

            back = serviceProvider.AddCommandHandler(GuidList.guidGitHubToolbarCmdSet, PkgCmdIDList.backCommand,
                () => !disabled && (navController?.HasBack ?? false),
                () => {
                    DisableButtons();
                    navController.Back();
                },
                true);

            forward = serviceProvider.AddCommandHandler(GuidList.guidGitHubToolbarCmdSet, PkgCmdIDList.forwardCommand,
                () => !disabled && (navController?.HasForward ?? false),
                () => {
                    DisableButtons();
                    navController.Forward();
                },
                true);

            refresh = serviceProvider.AddCommandHandler(GuidList.guidGitHubToolbarCmdSet, PkgCmdIDList.refreshCommand,
                () => !disabled,
                () => {
                    DisableButtons();
                    Refresh();
                },
                true);

            initialized = true;

            base.Initialize(serviceProvider);

            hosts.WhenAnyValue(x => x.IsLoggedInToAnyHost).Subscribe(_ => LoadDefault());
        }

        public void Initialize(ViewWithData data = null)
        {
            Title = "GitHub";
            Load(data).Forget();
        }

        void SetupNavigation()
        {
            if (navController != null)
                return;

            navController = new NavigationController(uiProvider);
            navController.WhenAnyValue(x => x.HasBack, y => y.HasForward).Subscribe(_ => UpdateToolbar());
        }

        protected override void RepoChanged(bool changed)
        {
            base.RepoChanged(changed);

            if (!initialized)
                return;

            if (!changed)
                return;

            Stop();
            RepositoryOrigin = RepositoryOrigin.Unknown;
            Refresh();
        }

        void LoadDefault()
        {
            Load(new ViewWithData(DefaultControllerFlow)).Forget();
        }

        void Refresh()
        {
            Load().Forget();
        }

        /// <summary>
        /// This method is reentrant, so all await calls need to be done before
        /// any actions are performed on the data. More recent calls to this method
        /// will cause previous calls pending on await calls to exit early.
        /// </summary>
        /// <returns></returns>
        async Task Load(ViewWithData data = null)
        {
            if (!initialized)
                return;

            latestReloadCallId++;
            var reloadCallId = latestReloadCallId;

            if (RepositoryOrigin == RepositoryOrigin.Unknown)
            {
                var origin = await GetRepositoryOrigin();
                if (reloadCallId != latestReloadCallId)
                    return;

                RepositoryOrigin = origin;
            }

            var connection = await connectionManager.LookupConnection(ActiveRepo);
            if (reloadCallId != latestReloadCallId)
                return;

            if (connection == null)
                IsLoggedIn = false;
            else
            {
                var isLoggedIn = await connection.IsLoggedIn(hosts);
                if (reloadCallId != latestReloadCallId)
                    return;

                IsLoggedIn = isLoggedIn;
            }

            Load(connection, data);
        }

        void Load(IConnection connection, ViewWithData data)
        {
            if (RepositoryOrigin == UI.RepositoryOrigin.NonGitRepository)
            {
                LoadSingleView(UIViewType.NotAGitRepository, data);
            }
            else if (RepositoryOrigin == UI.RepositoryOrigin.Other)
            {
                LoadSingleView(UIViewType.NotAGitHubRepository, data);
            }
            else if (!IsLoggedIn)
            {
                LoadSingleView(UIViewType.LoggedOut, data);
            }
            else
            {
                SetupNavigation();
                navController.LoadView(connection, data, view => Control = view);
            }
        }

        void LoadSingleView(UIViewType type, ViewWithData data = null)
        {
            Stop();
            Control = uiProvider.GetView(type, data);
        }

        void UpdateToolbar()
        {
            back.Enabled = navController?.HasBack ?? false;
            forward.Enabled = navController?.HasForward ?? false;
            refresh.Enabled = navController != null;
            disabled = false;
        }

        void DisableButtons()
        {
            disabled = true;
            back.Enabled = false;
            forward.Enabled = false;
            refresh.Enabled = false;
        }

        void Stop()
        {
            DisableButtons();
            navController = null;
            disposables.Clear();
            UpdateToolbar();
        }

        string title;
        [AllowNull]
        public string Title
        {
            get { return title; }
            set { title = value; this.RaisePropertyChange(); }
        }

        IView control;
        public IView Control
        {
            get { return control; }
            set { control = value; this.RaisePropertyChange(); }
        }

        bool isLoggedIn;
        public bool IsLoggedIn
        {
            get { return isLoggedIn; }
            set { isLoggedIn = value;  this.RaisePropertyChange(); }
        }

        public RepositoryOrigin RepositoryOrigin { get; private set; }

        string message;
        [AllowNull]
        public string Message
        {
            get { return message; }
            set { message = value; this.RaisePropertyChange(); }
        }

        MessageType messageType;
        [AllowNull]
        public MessageType MessageType
        {
            get { return messageType; }
            set { messageType = value; this.RaisePropertyChange(); }
        }

        public bool? IsGitHubRepo
        {
            get
            {
                return RepositoryOrigin == RepositoryOrigin.Unknown ?
                    (bool?)null :
                    RepositoryOrigin == UI.RepositoryOrigin.DotCom ||
                    RepositoryOrigin == UI.RepositoryOrigin.Enterprise;
            }
        }

        public ReactiveCommand<object> CancelCommand { get; private set; }
        public ICommand Cancel => CancelCommand;

        public bool IsShowing => true;

        bool disposed = false;
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!disposed)
                {
                    disposed = true;
                    DisableButtons();
                    disposables.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
