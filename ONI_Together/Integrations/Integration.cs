using System;

namespace ONI_Together.Integrations
{
    public abstract class Integration
    {
        protected bool ModActive = false;

        public void Initialize()
        {
            ModActive = InitializeTypes();

            if (ModActive)
            {
                VerifyTypes();
                ModActive = InitializeMethods();
            }

            OnInitialized();
        }

        protected abstract bool InitializeTypes();

        protected abstract bool InitializeMethods();

        protected abstract void VerifyTypes();

        protected abstract void OnInitialized();
    }
}