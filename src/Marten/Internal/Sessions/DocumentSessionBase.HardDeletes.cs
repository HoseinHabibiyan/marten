using System;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using Marten.Exceptions;
using Marten.Internal.Storage;
using Marten.Linq.Filters;
using Marten.Linq.SqlGeneration;

namespace Marten.Internal.Sessions
{
    public abstract partial class DocumentSessionBase
    {
        public void HardDelete<T>(T entity)
        {
            assertNotDisposed();
            var documentStorage = StorageFor<T>();

            var deletion = documentStorage.HardDeleteForDocument(entity, Tenant);
            _workTracker.Add(deletion);

            documentStorage.Eject(this, entity);
        }

        public void HardDelete<T>(int id)
        {
            assertNotDisposed();

            var storage = StorageFor<T>();

            if (storage is IDocumentStorage<T, int> i)
            {
                _workTracker.Add(i.HardDeleteForId(id, Tenant));

                ejectById<T>(id);
            }
            else if (storage is IDocumentStorage<T, long> l)
            {
                _workTracker.Add(l.HardDeleteForId(id, Tenant));

                ejectById<T>((long)id);
            }
            else
            {
                throw new DocumentIdTypeMismatchException(storage, typeof(int));
            }
        }

        public void HardDelete<T>(long id)
        {
            assertNotDisposed();
            var deletion = StorageFor<T, long>().HardDeleteForId(id, Tenant);
            _workTracker.Add(deletion);

            ejectById<T>(id);
        }

        public void HardDelete<T>(Guid id)
        {
            assertNotDisposed();
            var deletion = StorageFor<T, Guid>().HardDeleteForId(id, Tenant);
            _workTracker.Add(deletion);

            ejectById<T>(id);
        }

        public void HardDelete<T>(string id)
        {
            assertNotDisposed();

            var deletion = StorageFor<T, string>().HardDeleteForId(id, Tenant);
            _workTracker.Add(deletion);

            ejectById<T>(id);
        }

        public void HardDeleteWhere<T>(Expression<Func<T, bool>> expression)
        {
            assertNotDisposed();

            var documentStorage = StorageFor<T>();
            var deletion = new StatementOperation(documentStorage, documentStorage.HardDeleteFragment);
            deletion.ApplyFiltering(this, expression);

            _workTracker.Add(deletion);
        }

        /// <summary>
        /// For soft-deleted document types, this is a one sized fits all mechanism to reverse the
        /// soft deletion tracking
        /// </summary>
        /// <param name="expression"></param>
        /// <typeparam name="T"></typeparam>
        /// <exception cref="InvalidOperationException"></exception>
        public void UndoDeleteWhere<T>(Expression<Func<T, bool>> expression)
        {
            assertNotDisposed();


            var documentStorage = StorageFor<T>();
            if (documentStorage.DeleteFragment is HardDelete)
            {
                throw new InvalidOperationException("Un-deleting documents can only be done against document types configured to be soft-deleted");
            }
            var deletion = new StatementOperation(documentStorage, new UnSoftDelete(documentStorage));


            var @where = deletion.ApplyFiltering(this, expression);

            // This is hokey, but you need to remove the normally applied filter
            // to exclude soft-deleted documents because that's exactly what you do want
            // here
            if (@where is CompoundWhereFragment compound)
            {
                var filter = compound.Children.OfType<ExcludeSoftDeletedFilter>().FirstOrDefault();
                if (filter != null)
                {
                    compound.Remove(filter);
                }
            }


            _workTracker.Add(deletion);
        }

    }
}
