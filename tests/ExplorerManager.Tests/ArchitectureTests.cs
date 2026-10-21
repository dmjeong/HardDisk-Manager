using System;
using System.Linq;
using ExplorerManager.Presentation;
using Xunit;

namespace ExplorerManager.Tests
{
    public sealed class ArchitectureTests
    {
        [Fact] public void PresentationDoesNotReferenceInfrastructureOrWpf()
        {
            string[] names = typeof(MainViewModel).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
            Assert.DoesNotContain("ExplorerManager.Infrastructure", names);
            Assert.DoesNotContain("PresentationFramework", names);
            Assert.DoesNotContain("System.Data", names);
        }
        [Fact] public void DomainHasNoProjectDependencies()
        {
            Assert.DoesNotContain(typeof(ExplorerManager.Domain.DrivePolicy).Assembly.GetReferencedAssemblies(), a => a.Name.StartsWith("ExplorerManager."));
        }
    }
}
