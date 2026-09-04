using WinZ3805A.Device.Commands;
using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// Every editor in the Advanced Console must open on a value the validator accepts (#404).
/// </summary>
/// <remarks>
/// <b>This is the check that was missing three times.</b> §10.8's duration limit opened on 1 when
/// the receiver held 86 400; §10.5's mask opened on 10, which happened to match this unit and so
/// looked right; and <c>:SYST:COMM:SER1:BAUD</c> opened on 0 against a parameter whose legal values
/// are 1200, 2400, 9600 and 19200. The last one disabled Send the instant the command was picked,
/// with the error already on screen, which reads as the console refusing the command.
///
/// A default is the one value nobody re-reads, so it is exactly the thing to assert mechanically
/// against the real catalog rather than to review.
/// </remarks>
public sealed class ParameterDefaultsTests
{
    public static TheoryData<string, ParameterSpec> EveryParameter()
    {
        TheoryData<string, ParameterSpec> data = new();

        foreach (ConsoleCommand command in new ConsoleCatalog(new SmartClockDriver(new FakeTimeProvider())).All)
        {
            foreach (ParameterSpec parameter in command.Parameters)
            {
                data.Add(command.Mnemonic, parameter);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryParameter))]
    public void EveryEditorOpensOnAValueTheValidatorAccepts(string mnemonic, ParameterSpec parameter)
    {
        string? opening = ParameterDefaults.For(parameter);

        // A PRN list has no honest default - any satellite it invented would be one the user did
        // not ask for - so it opens empty and the validator's "required" is the correct answer.
        if (parameter.Kind == ParameterKind.PrnList)
        {
            Assert.Equal(string.Empty, opening);
            return;
        }

        ConsoleArgument.Result result = ConsoleArgument.For(parameter, opening);

        Assert.True(
            result.Error is null,
            $"{mnemonic} opens its '{parameter.Name}' editor on '{opening}', which the validator " +
            $"rejects: {result.Error}. A command is invalid the moment it is picked, and Send is " +
            "disabled before the user has touched anything.");
    }

    [Fact]
    public void AConstrainedParameterOpensOnOneOfItsChoices()
    {
        // The specific shape that broke: a numeric kind carrying a set of legal values. The editor
        // used to switch on the kind alone and put a NumberBox in front of it.
        ParameterSpec baud = new("Baud", ParameterKind.Integer, Choices: ["1200", "2400", "9600", "19200"]);

        Assert.Equal("1200", ParameterDefaults.For(baud));
    }

    [Fact]
    public void ARangedParameterOpensOnItsMinimum()
    {
        ParameterSpec bits = new("Data bits", ParameterKind.Integer, Minimum: 7, Maximum: 8);

        Assert.Equal("7", ParameterDefaults.For(bits));
    }
}
