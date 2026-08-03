using Microsoft.Extensions.DependencyInjection;

namespace LJExport
{
    public partial class App : Application
    {
        public App(AppShell appShell)
        {
            InitializeComponent();
            this.appShell = appShell;
        }

        private readonly AppShell appShell;

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(appShell);
        }
    }
}