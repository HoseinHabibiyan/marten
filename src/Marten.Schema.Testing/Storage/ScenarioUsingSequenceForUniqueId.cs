using System;
using System.Collections.Generic;
using System.Linq;
using Marten.Storage;
using Xunit;

namespace Marten.Schema.Testing.Storage
{
    public class ScenarioUsingSequenceForUniqueId : IntegrationContext
    {
        #region sample_scenario-usingsequenceforuniqueid-setup
        // We introduce a new feature schema, making use of Marten's schema management facilities.
        public class MatterId: FeatureSchemaBase
        {
            private readonly int _startFrom;
            private readonly string _schema;

            public MatterId(StoreOptions options, int startFrom) : base(nameof(MatterId), options)
            {
                _startFrom = startFrom;
                _schema = options.DatabaseSchemaName;
            }

            protected override IEnumerable<ISchemaObject> schemaObjects()
            {
                // We return a sequence that starts from the value provided in the ctor
                yield return new Sequence(new DbObjectName(_schema, $"mt_{nameof(MatterId).ToLowerInvariant()}"), _startFrom);
            }
        }
        #endregion sample_scenario-usingsequenceforuniqueid-setup

        [Fact]
        public void ScenarioUsingSequenceForUniqueIdScenario()
        {
            StoreOptions(storeOptions =>
            {
                #region sample_scenario-usingsequenceforuniqueid-storesetup-1
                storeOptions.Storage.Add(new MatterId(storeOptions, 10000));
                #endregion sample_scenario-usingsequenceforuniqueid-storesetup-1
            });

            #region sample_scenario-usingsequenceforuniqueid-storesetup-2
            theStore.Schema.ApplyAllConfiguredChangesToDatabase();
            #endregion sample_scenario-usingsequenceforuniqueid-storesetup-2

            #region sample_scenario-usingsequenceforuniqueid-querymatter
            var matter = theStore.Storage.FindFeature(typeof(MatterId)).Objects.OfType<Sequence>().Single();

            using (var session = theStore.OpenSession())
            {
                // Generate a new, unique identifier
                var nextMatter = session.NextInSequence(matter);

                var contract = new Contract
                {
                    Id = Guid.NewGuid(),
                    Matter = nextMatter
                };

                var inquiry = new Inquiry
                {
                    Id = Guid.NewGuid(),
                    Matter = nextMatter
                };

                session.Store(contract);
                session.Store(inquiry);

                session.SaveChanges();
            }
            #endregion sample_scenario-usingsequenceforuniqueid-querymatter
        }

        #region sample_scenario-usingsequenceforuniqueid-setup-types
        public class Contract
        {
            public Guid Id { get; set; }
            public int Matter { get; set; }
            // Other fields...
        }
        public class Inquiry
        {
            public Guid Id { get; set; }
            public int Matter { get; set; }
            // Other fields...
        }
        #endregion sample_scenario-usingsequenceforuniqueid-setup-types

    }
    #region sample_scenario-usingsequenceforuniqueid-setup-extensions
    public static class SessionExtensions
    {
        // A shorthand for generating the required SQL statement for a sequence value query
        public static int NextInSequence(this IQuerySession session, Sequence sequence)
        {
            return session.Query<int>("select nextval(?)", sequence.Identifier.QualifiedName).First();
        }
    }
    #endregion sample_scenario-usingsequenceforuniqueid-setup-extensions
}
