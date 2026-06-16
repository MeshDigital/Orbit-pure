using System;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace SLSKDONET.Tests
{
    public class InspectSoulseekTypesTests
    {
        private readonly ITestOutputHelper _output;

        public InspectSoulseekTypesTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void InspectTransferTypes()
        {
            _output.WriteLine("=== REFLECTION INSPECTION ===");
            
            // Inspect TransferOptions
            var transferOptionsType = typeof(Soulseek.TransferOptions);
            _output.WriteLine($"Type: {transferOptionsType.FullName}");
            foreach (var prop in transferOptionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                _output.WriteLine($"  Property: {prop.PropertyType.Name} {prop.Name}");
            }
            foreach (var ctor in transferOptionsType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                _output.WriteLine($"  Constructor: {ctor}");
            }

            // Inspect Transfer
            var transferType = typeof(Soulseek.Transfer);
            _output.WriteLine($"Type: {transferType.FullName}");
            foreach (var prop in transferType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                _output.WriteLine($"  Property: {prop.PropertyType.Name} {prop.Name}");
            }

            // Inspect TransferStateChangedEventArgs if it exists, or look at how the stateChange delegate is defined
            // Let's find constructor parameter types for TransferOptions
        }
    }
}
