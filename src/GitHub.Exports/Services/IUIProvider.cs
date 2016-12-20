using System;
using System.Threading.Tasks;
using GitHub.App.Factories;
using GitHub.Exports;
using GitHub.Models;
using GitHub.UI;

namespace GitHub.Services
{
    public interface IUIProvider
    {
        IUIController Configure(UIControllerFlow flow, IConnection connection = null, ViewWithData data = null);
        IUIController Run(UIControllerFlow flow);
        void RunInDialog(UIControllerFlow flow, IConnection connection = null);
        void RunInDialog(IUIController controller);
        IView GetView(UIViewType which, ViewWithData data);
        void StopUI(IUIController controller);
    }
}