using DeskIsle.Core;
using Xunit;
namespace DeskIsle.Core.Tests;
public sealed class ReferenceOrderTests
{
    [Theory]
    [InlineData(0,5,"BCDEA")][InlineData(4,0,"EABCD")][InlineData(1,4,"ACDBE")]
    [InlineData(3,1,"ADBCE")][InlineData(2,2,"ABCDE")][InlineData(2,3,"ABCDE")]
    public void MovesIntoExpectedGapWithoutMutatingSource(int from,int slot,string expected)
    {
        var source = "ABCDE".ToCharArray(); var moved = ReferenceOrder.Move(source, from, slot);
        Assert.Equal(expected, new string(moved.ToArray())); Assert.Equal("ABCDE", new string(source));
    }
    [Theory][InlineData(-1,0)][InlineData(5,0)][InlineData(0,-1)][InlineData(0,6)]
    public void InvalidPositionsCannotChangeOrder(int from,int slot) => Assert.Throws<ArgumentOutOfRangeException>(() => ReferenceOrder.Move("ABCDE".ToCharArray(),from,slot));
}
