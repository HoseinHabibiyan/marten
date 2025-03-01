using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Marten.Events;
using Marten.Internal.Operations;
using Marten.Linq.Filters;
using Marten.Linq.SqlGeneration;
using Marten.Patching;
using Marten.Services;
using Marten.Storage;

namespace Marten.Internal.Sessions
{
    public abstract partial class DocumentSessionBase: QuerySession, IDocumentSession
    {
        internal readonly ISessionWorkTracker _workTracker;


        protected DocumentSessionBase(DocumentStore store, SessionOptions sessionOptions, IManagedConnection database,
            ITenant tenant): base(store, sessionOptions, database, tenant)
        {
            Concurrency = sessionOptions.ConcurrencyChecks;
            _workTracker = new UnitOfWork(this);

            Events = new EventStore(this, store, tenant);
            _workTracker = new UnitOfWork(this);
        }

        internal DocumentSessionBase(DocumentStore store, SessionOptions sessionOptions, IManagedConnection database,
            ITenant tenant, ISessionWorkTracker workTracker): base(store, sessionOptions, database, tenant)
        {
            Concurrency = sessionOptions.ConcurrencyChecks;
            _workTracker = new UnitOfWork(this);

            Events = new EventStore(this, store, tenant);

            _workTracker = workTracker;
        }

        internal ITenancy Tenancy => DocumentStore.As<DocumentStore>().Tenancy;

        internal ISessionWorkTracker WorkTracker => _workTracker;


        public void Store<T>(IEnumerable<T> entities)
        {
            Store(entities?.ToArray());
        }

        public void Store<T>(params T[] entities)
        {
            if (entities == null)
                throw new ArgumentNullException(nameof(entities));

            if (typeof(T).IsGenericEnumerable())
                throw new ArgumentOutOfRangeException(typeof(T).Name,
                    "Do not use IEnumerable<T> here as the document type. Either cast entities to an array instead or use the IEnumerable<T> Store() overload instead.");

            store(entities);
        }

        public void Store<T>(T entity, Guid version)
        {
            assertNotDisposed();

            var storage = StorageFor<T>();
            storage.Store(this, entity, version);
            var op = storage.Upsert(entity, this, Tenant);
            _workTracker.Add(op);
        }

        public void Insert<T>(IEnumerable<T> entities)
        {
            Insert(entities.ToArray());
        }

        public void Insert<T>(params T[] entities)
        {
            assertNotDisposed();

            if (entities == null) throw new ArgumentNullException(nameof(entities));

            if (typeof(T).IsGenericEnumerable())
                throw new ArgumentOutOfRangeException(typeof(T).Name,
                    "Do not use IEnumerable<T> here as the document type. You may need to cast entities to an array instead.");

            if (typeof(T) == typeof(object))
            {
                InsertObjects(entities.OfType<object>());
            }
            else
            {
                var storage = StorageFor<T>();

                foreach (var entity in entities)
                {
                    storage.Store(this, entity);
                    var op = storage.Insert(entity, this, Tenant);
                    _workTracker.Add(op);
                }
            }
        }

        public void Update<T>(IEnumerable<T> entities)
        {
            Update(entities.ToArray());
        }

        public void Update<T>(params T[] entities)
        {
            assertNotDisposed();

            if (entities == null) throw new ArgumentNullException(nameof(entities));

            if (typeof(T).IsGenericEnumerable())
                throw new ArgumentOutOfRangeException(typeof(T).Name,
                    "Do not use IEnumerable<T> here as the document type. You may need to cast entities to an array instead.");

            if (typeof(T) == typeof(object))
            {
                InsertObjects(entities.OfType<object>());
            }
            else
            {
                var storage = StorageFor<T>();

                foreach (var entity in entities)
                {
                    storage.Store(this, entity);
                    var op = storage.Update(entity, this, Tenant);
                    _workTracker.Add(op);
                }
            }
        }

        public void InsertObjects(IEnumerable<object> documents)
        {
            assertNotDisposed();

            documents.Where(x => x != null).GroupBy(x => x.GetType()).Each(group =>
            {
                var handler = typeof(InsertHandler<>).CloseAndBuildAs<IHandler>(group.Key);
                handler.Store(this, group);
            });
        }

        public IUnitOfWork PendingChanges => _workTracker;

        public void StoreObjects(IEnumerable<object> documents)
        {
            assertNotDisposed();

            var documentsGroupedByType = documents
                .Where(x => x != null)
                .GroupBy(x => x.GetType());

            foreach (var group in documentsGroupedByType)
            {
                // Build the right handler for the group type
                var handler = typeof(Handler<>).CloseAndBuildAs<IHandler>(group.Key);
                handler.Store(this, group);
            }
        }

        public IEventStore Events { get; }

        public IPatchExpression<T> Patch<T>(int id)
        {
            return patchById<T>(id);
        }

        public IPatchExpression<T> Patch<T>(long id)
        {
            return patchById<T>(id);
        }

        public IPatchExpression<T> Patch<T>(string id)
        {
            return patchById<T>(id);
        }

        public IPatchExpression<T> Patch<T>(Guid id)
        {
            return patchById<T>(id);
        }

        public IPatchExpression<T> Patch<T>(Expression<Func<T, bool>> filter)
        {
            assertNotDisposed();

            return new PatchExpression<T>(filter, this);
        }

        public IPatchExpression<T> Patch<T>(ISqlFragment fragment)
        {
            assertNotDisposed();

            return new PatchExpression<T>(fragment, this);
        }


        public void QueueOperation(IStorageOperation storageOperation)
        {
            _workTracker.Add(storageOperation);
        }

        public virtual void Eject<T>(T document)
        {
            StorageFor<T>().Eject(this, document);
            _workTracker.Eject(document);

            ChangeTrackers.RemoveAll(x => ReferenceEquals(document, x.Document));
        }

        public virtual void EjectAllOfType(Type type)
        {
            ItemMap.Remove(type);
            ChangeTrackers.RemoveAll(x => x.Document.GetType().CanBeCastTo(type));
        }

        public void SetHeader(string key, object value)
        {
            Headers ??= new Dictionary<string, object>();

            Headers[key] = value;
        }

        public object GetHeader(string key)
        {
            return Headers?[key];
        }

        private Dictionary<string, NestedTenantSession> _byTenant;

        /// <summary>
        /// Access data from another tenant and apply document or event updates to this
        /// IDocumentSession for a separate tenant
        /// </summary>
        /// <param name="tenantId"></param>
        /// <returns></returns>
        public ITenantOperations ForTenant(string tenantId)
        {
            if (_byTenant == null)
            {
                _byTenant = new Dictionary<string, NestedTenantSession>();
            }

            if (_byTenant.TryGetValue(tenantId, out var tenantSession))
            {
                return tenantSession;
            }

            var tenant = Options.Tenancy[tenantId];
            tenantSession = new NestedTenantSession(this, tenant);
            _byTenant[tenantId] = tenantSession;

            return tenantSession;
        }

        protected internal abstract void ejectById<T>(long id);
        protected internal abstract void ejectById<T>(int id);
        protected internal abstract void ejectById<T>(Guid id);
        protected internal abstract void ejectById<T>(string id);

        protected internal virtual void processChangeTrackers()
        {
            // Nothing
        }

        protected internal virtual void resetDirtyChecking()
        {
            // Nothing
        }


        private void store<T>(IEnumerable<T> entities)
        {
            assertNotDisposed();

            if (typeof(T) == typeof(object))
            {
                StoreObjects(entities.OfType<object>());
            }
            else
            {
                var storage = StorageFor<T>();

                if (Concurrency == ConcurrencyChecks.Disabled && storage.UseOptimisticConcurrency)
                {
                    foreach (var entity in entities)
                    {
                        // Put it in the identity map -- if necessary
                        storage.Store(this, entity);

                        var overwrite = storage.Overwrite(entity, this, Tenant);

                        _workTracker.Add(overwrite);
                    }
                }
                else
                {
                    foreach (var entity in entities)
                    {
                        // Put it in the identity map -- if necessary
                        storage.Store(this, entity);

                        var upsert = storage.Upsert(entity, this, Tenant);

                        _workTracker.Add(upsert);
                    }
                }
            }
        }

        private IPatchExpression<T> patchById<T>(object id)
        {
            assertNotDisposed();

            var where = new WhereFragment("d.id = ?", id);
            return new PatchExpression<T>(where, this);
        }

        public void EjectPatchedTypes(IUnitOfWork changes)
        {
            var patchedTypes = changes.Patches().Select(x => x.DocumentType).Distinct().ToArray();
            foreach (var type in patchedTypes) EjectAllOfType(type);
        }

        internal interface IHandler
        {
            void Store(IDocumentSession session, IEnumerable<object> objects);
        }

        internal class Handler<T>: IHandler
        {
            public void Store(IDocumentSession session, IEnumerable<object> objects)
            {
                // Delegate to the Store<T>() method
                session.Store(objects.OfType<T>().ToArray());
            }
        }

        internal class InsertHandler<T>: IHandler
        {
            public void Store(IDocumentSession session, IEnumerable<object> objects)
            {
                session.Insert(objects.OfType<T>().ToArray());
            }
        }
    }
}
