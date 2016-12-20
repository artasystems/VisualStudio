using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using GitHub.Extensions;
using GitHub.Helpers;
using GitHub.Models;
using GitHub.Services;
using GitHub.UI;
using NLog;
using NullGuard;
using ReactiveUI;
using GitHub.App.Factories;
using GitHub.Exports;
using GitHub.Controllers;

namespace GitHub.VisualStudio.UI
{
    [Export(typeof(IUIProvider))]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    [NullGuard(ValidationFlags.None)]
    public class UIProvider : IUIProvider
    {
        static readonly Logger log = LogManager.GetCurrentClassLogger();

        WindowController windowController;

        readonly CompositeDisposable disposables = new CompositeDisposable();

        readonly IGitHubServiceProvider serviceProvider;
        readonly IExportFactoryProvider exportFactory;

        [ImportingConstructor]
        public UIProvider(IGitHubServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
            exportFactory = serviceProvider.TryGetService<IExportFactoryProvider>();
        }

        public IView GetView(UIViewType which, ViewWithData data)
        {
            var uiFactory = serviceProvider.GetService<IUIFactory>();
            var pair = uiFactory.CreateViewAndViewModel(which);
            pair.ViewModel.Initialize(data);
            pair.View.DataContext = pair.ViewModel;
            return pair.View;
        }

        public IUIController Configure(UIControllerFlow flow, IConnection connection = null, ViewWithData data = null)
        {
            var controller = new UIController(serviceProvider);
            disposables.Add(controller);
            var listener = controller.Configure(flow, connection, data).Publish().RefCount();

            listener.Subscribe(_ => { }, () =>
            {
                StopUI(controller);
            });

            // if the flow is authentication, we need to show the login dialog. and we can't
            // block the main thread on the subscriber, it'll block other handlers, so we're doing
            // this on a separate thread and posting the dialog to the main thread
            listener
                .Where(c => c.Flow == UIControllerFlow.Authentication)
                .ObserveOn(RxApp.TaskpoolScheduler)
                .Subscribe(c =>
                {
                    // nothing to do, we already have a dialog
                    if (windowController != null)
                        return;
                    RunModalDialogForAuthentication(c.Flow, listener, c).Forget();
                });

            return controller;
        }

        public IUIController Run(UIControllerFlow flow)
        {
            var controller = Configure(flow);
            controller.Start();
            return controller;
        }

        public void RunInDialog(UIControllerFlow flow, IConnection connection = null)
        {
            var controller = Configure(flow, connection);
            RunInDialog(controller);
        }

        public void RunInDialog(IUIController controller)
        {
            var listener = controller.TransitionSignal;
            var flow = controller.SelectedFlow;

            windowController = new UI.WindowController(listener);
            windowController.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
            EventHandler stopUIAction = (s, e) =>
            {
                StopUI(controller);
            };
            windowController.Closed += stopUIAction;
            listener.Subscribe(_ => { }, () =>
            {
                windowController.Closed -= stopUIAction;
                windowController.Close();
                StopUI(controller);
            });

            controller.Start();
            windowController.ShowModal();
            windowController = null;
        }

        public void StopUI(IUIController controller)
        {
            try
            {
                if (!controller.IsStopped)
                    controller.Stop();
                disposables.Remove(controller);
            }
            catch (Exception ex)
            {
                log.Error("Failed to dispose UI. {0}", ex);
            }
        }

        async Task RunModalDialogForAuthentication(UIControllerFlow flow, IObservable<LoadData> listener, LoadData initiaLoadData)
        {
            await ThreadingHelper.SwitchToMainThreadAsync();
            windowController = new WindowController(listener,
                (v, f) => f == flow,
                (v, f) => f != flow);
            windowController.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            windowController.Load(initiaLoadData.View);
            windowController.ShowModal();
            windowController = null;
        }

        bool disposed;
        protected void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (disposed) return;

                if (disposables != null)
                    disposables.Dispose();
                disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }


        //Dictionary<UIControllerFlow, IUIController> controllers = new Dictionary<UIControllerFlow, IUIController>();
        //void Run([AllowNull] IConnection connection)
        //{
        //    var flow = UIControllerFlow.PullRequestList;
        //    IUIController uiController = null;
        //    IObservable<LoadData> creation = null;
        //    if (!controllers.ContainsKey(flow))
        //    {
        //        var uiProvider = ServiceProvider.GetService<IGitHubServiceProvider>();
        //        var factory = uiProvider.GetService<IExportFactoryProvider>();
        //        var uiflow = factory.UIControllerFactory.CreateExport();
        //        disposables.Add(uiflow);
        //        uiController = uiflow.Value;
        //        uiController.Configure(flow).Publish().RefCount();
        //        controllers.Add(flow, uiController);

        //        // if the flow is authentication, we need to show the login dialog. and we can't
        //        // block the main thread on the subscriber, it'll block other handlers, so we're doing
        //        // this on a separate thread and posting the dialog to the main thread
        //        creation
        //            .Where(c => uiController.CurrentFlow == UIControllerFlow.Authentication)
        //            .ObserveOn(RxApp.TaskpoolScheduler)
        //            .Subscribe(c =>
        //            {
        //                // nothing to do, we already have a dialog
        //                if (windowController != null)
        //                    return;
        //                ShowModalDialog(uiController, creation, c).Forget();
        //            });

        //        creation
        //            .Where(c => uiController.CurrentFlow != UIControllerFlow.Authentication)
        //            .Subscribe(c =>
        //            {
        //                //if (!navigatingViaArrows)
        //                //{
        //                //    if (c.Direction == LoadDirection.Forward)
        //                //        GoForward(c.Data);
        //                //    else if (c.Direction == LoadDirection.Back)
        //                //        GoBack();
        //                //}
        //                //UpdateToolbar();

        //                //Control = c.View;
        //            });
        //    }

        //    uiController.Start();
        //}

        //async Task ShowModalDialog(IUIController controller, IObservable<LoadData> listener, LoadData initiaLoadData)
        //{
        //    await ThreadingHelper.SwitchToMainThreadAsync();
        //    windowController = new WindowController(listener,
        //        __ => controller.CurrentFlow == UIControllerFlow.Authentication,
        //        ___ => controller.CurrentFlow != UIControllerFlow.Authentication);
        //    windowController.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        //    windowController.Load(initiaLoadData.View);
        //    windowController.ShowModal();
        //    windowController = null;
        //}

        //void StartFlow(UIControllerFlow controllerFlow, [AllowNull]IConnection conn, ViewWithData data = null)
        //{
        //    Stop();

        //    if (conn == null)
        //        return;

        //    var uiProvider = ServiceProvider.GetService<IGitHubServiceProvider>();
        //    var factory = uiProvider.GetService<IExportFactoryProvider>();
        //    var uiflow = factory.UIControllerFactory.CreateExport();
        //    disposables.Add(uiflow);
        //    uiController = uiflow.Value;
        //    var creation = uiController.SelectFlow(controllerFlow).Publish().RefCount();

        //    // if the flow is authentication, we need to show the login dialog. and we can't
        //    // block the main thread on the subscriber, it'll block other handlers, so we're doing
        //    // this on a separate thread and posting the dialog to the main thread
        //    creation
        //        .Where(c => uiController.CurrentFlow == UIControllerFlow.Authentication)
        //        .ObserveOn(RxApp.TaskpoolScheduler)
        //        .Subscribe(c =>
        //        {
        //            // nothing to do, we already have a dialog
        //            if (windowController != null)
        //                return;
        //            //syncContext.Post(_ =>
        //            //{
        //            //    windowController = new WindowController(creation,
        //            //        __ => uiController.CurrentFlow == UIControllerFlow.Authentication,
        //            //        ___ => uiController.CurrentFlow != UIControllerFlow.Authentication);
        //            //    windowController.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        //            //    windowController.Load(c.View);
        //            //    windowController.ShowModal();
        //            //    windowController = null;
        //            //}, null);
        //        });

        //    creation
        //        .Where(c => uiController.CurrentFlow != UIControllerFlow.Authentication)
        //        .Subscribe(c =>
        //        {
        //            //if (!navigatingViaArrows)
        //            //{
        //            //    if (c.Direction == LoadDirection.Forward)
        //            //        GoForward(c.Data);
        //            //    else if (c.Direction == LoadDirection.Back)
        //            //        GoBack();
        //            //}
        //            //UpdateToolbar();

        //            //Control = c.View;
        //        });

        //    if (data != null)
        //        uiController.LoadView(data);
        //    uiController.Start(conn);
        //}

        //void Stop()
        //{
        //    if (uiController == null)
        //        return;

        //    //DisableButtons();
        //    //windowController?.Close();
        //    //uiController.Stop();
        //    //disposables.Clear();
        //    //uiController = null;
        //    //currentNavItem = -1;
        //    //navStack.Clear();
        //    //UpdateToolbar();
        //}

        //        public IObservable<LoadData> SetupUI(UIControllerFlow controllerFlow, [AllowNull] IConnection connection)
        //        {
        //            StopUI();

        //            var uiProvider = ServiceProvider.GetService<IGitHubServiceProvider>();
        //            var factory = uiProvider.GetService<IExportFactoryProvider>();
        //            var uiflow = factory.UIControllerFactory.CreateExport();
        //            disposables.Add(uiflow);
        //            var uiController = uiflow.Value;
        //            var creation = uiController.Configure(controllerFlow).Publish().RefCount();

        //            windowController = new UI.WindowController(creation);
        //            windowController.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
        //            windowController.Closed += StopUIFlowWhenWindowIsClosedByUser;
        //            creation.Subscribe(c => { }, () =>
        //            {
        //                windowController.Closed -= StopUIFlowWhenWindowIsClosedByUser;
        //                windowController.Close();
        //                if (currentUIFlow != disposable)
        //                    StopUI(disposable);
        //                else
        //                    StopUI();
        //            });
        //            ui.Start();
        //            return creation;
        //        }

        //        public IObservable<bool> ListenToCompletionState()
        //        {
        //            var ui = currentUIFlow?.Value;
        //            if (ui == null)
        //            {
        //                log.Error("UIProvider:ListenToCompletionState:Cannot call ListenToCompletionState without calling SetupUI first");
        //#if DEBUG
        //                throw new InvalidOperationException("Cannot call ListenToCompletionState without calling SetupUI first");
        //#endif
        //            }
        //            return ui?.ListenToCompletionState() ?? Observable.Return(false);
        //        }

        //        public void RunUI()
        //        {
        //            Debug.Assert(windowController != null, "WindowController is null, did you forget to call SetupUI?");
        //            if (windowController == null)
        //            {
        //                log.Error("WindowController is null, cannot run UI.");
        //                return;
        //            }
        //            try
        //            {
        //                windowController.ShowModal();
        //            }
        //            catch (Exception ex)
        //            {
        //                log.Error("WindowController ShowModal failed. {0}", ex);
        //            }
        //        }

        //        public void RunUI(UIControllerFlow controllerFlow, [AllowNull] IConnection connection)
        //        {
        //            SetupUI(controllerFlow, connection);
        //            try
        //            {
        //                windowController.ShowModal();
        //            }
        //            catch (Exception ex)
        //            {
        //                log.Error("WindowController ShowModal failed for {0}. {1}", controllerFlow, ex);
        //            }
        //        }

        //        public void StopUI()
        //        {
        //            StopUI(currentUIFlow);
        //            currentUIFlow = null;
        //        }

        //        static void StopUI(ExportLifetimeContext<IUIController> disposable)
        //        {
        //            try
        //            {
        //                if (disposable != null && disposable.Value != null)
        //                {
        //                    if (!disposable.Value.IsStopped)
        //                        disposable.Value.Stop();
        //                    disposable.Dispose();
        //                }
        //            }
        //            catch (Exception ex)
        //            {
        //                log.Error("Failed to dispose UI. {0}", ex);
        //            }
        //        }

        //        void StopUIFlowWhenWindowIsClosedByUser(object sender, EventArgs e)
        //        {
        //            StopUI();
        //        }
    }
}
