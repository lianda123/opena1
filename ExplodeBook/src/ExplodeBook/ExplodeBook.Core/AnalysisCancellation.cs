using System;
using System.Diagnostics;
using Rhino;

namespace ExplodeBook.Core;

internal static class AnalysisCancellation
{
    private static bool cancelled;
    private static int depth;
    private static long lastPump;
    private sealed class Scope:IDisposable
    { public void Dispose(){if(--depth==0)RhinoApp.EscapeKeyPressed-=OnEscape;} }
    private static void OnEscape(object sender,EventArgs e)=>cancelled=true;
    public static IDisposable Begin()
    {if(depth++==0){cancelled=false;lastPump=Stopwatch.GetTimestamp();RhinoApp.EscapeKeyPressed+=OnEscape;}return new Scope();}
    public static bool Check()
    {
        long now=Stopwatch.GetTimestamp();
        if(now-lastPump>Stopwatch.Frequency/20){lastPump=now;RhinoApp.Wait();}
        return cancelled;
    }
}
