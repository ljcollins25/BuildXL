// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;

namespace BuildXL.Cache.ContentStore.Vfs
{
    /// <summary>
    /// A store which virtualizes calls to an underlying content store (i.e. content will
    /// be lazily materialized using the projected file system filter driver)
    /// </summary>
    public class VirtualizedContentStore : StartupShutdownBase, IContentStore
    {
        private IContentStore InnerStore { get; }

        internal VirtualizationRegistry Overlay { get; }

        protected override Tracer Tracer { get; } = new Tracer(nameof(VirtualizedContentStore));

        /// <nodoc />
        public VirtualizedContentStore(IContentStore innerStore)
        {
            // Create long-lived session to be used with overlay (ImplicitPin=false to avoid cache full errors)
            InnerStore = innerStore;
        }

        /// <inheritdoc />
        public CreateSessionResult<IReadOnlyContentSession> CreateReadOnlySession(Context context, string name, ImplicitPin implicitPin)
        {
            return CreateSession<IReadOnlyContentSession>(context, name, implicitPin);
        }

        /// <inheritdoc />
        public CreateSessionResult<IContentSession> CreateSession(Context context, string name, ImplicitPin implicitPin)
        {
            return CreateSession<IContentSession>(context, name, implicitPin);
        }

        private CreateSessionResult<T> CreateSession<T>(Context context, string name, ImplicitPin implicitPin)
            where T : class, IName
        {
            var operationContext = OperationContext(context);
            return operationContext.PerformOperation(
                Tracer,
                () =>
                {
                    var innerSessionResult = InnerStore.CreateSession(context, name, implicitPin).ThrowIfFailure();
                    var session = new VirtualizedContentSession(this, innerSessionResult.Session, name);
                    return new CreateSessionResult<T>(session as T);
                });
        }

        /// <inheritdoc />
        public Task<GetStatsResult> GetStatsAsync(Context context)
        {
            return InnerStore.GetStatsAsync(context);
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> StartupCoreAsync(OperationContext context)
        {
            await InnerStore.StartupAsync(context).ThrowIfFailure();

            return await base.StartupCoreAsync(context);
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> ShutdownCoreAsync(OperationContext context)
        {
            // Close all sessions?
            var result = await base.ShutdownCoreAsync(context);

            result &= await InnerStore.ShutdownAsync(context);

            return result;
        }
    }
}
