using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using System.Runtime.Loader;
class CatalogChecks
{
 static int Main(string[] args)
 {
  string root=args[0],dll=args[1];
  AssemblyLoadContext.Default.Resolving+=(c,n)=> {
   string[] paths={Path.GetFullPath(args[2])+Path.DirectorySeparatorChar};
   foreach(var p in paths)if(File.Exists(p+n.Name+".dll"))return c.LoadFromAssemblyPath(p+n.Name+".dll");return null;};
  var assembly=Assembly.LoadFrom(dll);
  var type=assembly.GetType("LSImmersiveLife.LSPDDispatchEventDefinition",true);
  var parse=type.GetMethod("FromXml",BindingFlags.Static|BindingFlags.NonPublic);
  int count=0,errors=0;
  foreach(var e in XDocument.Load(Path.Combine(root,"LSPDDispatchEvent.xml")).Descendants("Event"))
  {
   try {
    var definition=parse.Invoke(null,new object[]{e});
    var choose=type.GetMethod("ChooseSuspectCount",BindingFlags.Instance|BindingFlags.NonPublic);
    var random=new Random(7);
    var seen=new System.Collections.Generic.HashSet<int>();
    for(int i=0;i<80;i++) {
     int n=(int)choose.Invoke(definition,new object[]{random,5});
     int lo=(int)type.GetProperty("MinimumSuspects",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(definition);
     int hi=(int)type.GetProperty("MaximumSuspects",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(definition);
     seen.Add(n);
     if(n<lo||n>Math.Min(5,hi))throw new Exception("Invalid sampled suspect count "+n);
    }
    int authoredMax=(int)type.GetProperty("MaximumSuspects",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(definition);
    int authoredMin=(int)type.GetProperty("MinimumSuspects",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(definition);
    if(authoredMax==5 && (!seen.Contains(5) || (authoredMin<=4 && !seen.Contains(4))))throw new Exception("Four/five suspect branch was never selected");
    count++;
   } catch(Exception ex){errors++;Console.WriteLine(e.Attribute("id")+": "+(ex.InnerException??ex).Message);}
  }
  Console.WriteLine("Parsed valid event definitions: "+count+"; failures: "+errors);
  var original=XDocument.Load(Path.Combine(root,"LSPDDispatchEvent.xml")).Descendants("Event").First();
  var bad=new XElement(original); bad.SetAttributeValue("preparedActivity","invented_action");
  try { parse.Invoke(null,new object[]{bad}); errors++; Console.WriteLine("Invalid action was accepted"); }
  catch(TargetInvocationException) { Console.WriteLine("Invalid activity rejected"); }
  try { CheckAudio(assembly,root); Console.WriteLine("Audio aliases, occurrence deduplication and closed-scope checks passed"); }
  catch(Exception ex){errors++;Console.WriteLine("Audio test failed: "+(ex.InnerException??ex));}
  return errors==0?0:1;
 }
 static object Call(object obj,string name,params object[] args)
 {return obj.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).Invoke(obj,args);}
 static void CheckAudio(Assembly assembly,string root)
 {
  string temp=Path.Combine(Path.GetTempPath(),"ls-dispatch-checks-"+Guid.NewGuid().ToString("N"));
  string data=Path.Combine(temp,"scripts","LSImmersiveLife");Directory.CreateDirectory(data);
  File.Copy(Path.Combine(root,"ResponseAudio.xml"),Path.Combine(data,"ResponseAudio.xml"));
  Directory.CreateSymbolicLink(Path.Combine(data,"Audio"),Path.Combine(root,"Audio"));
  object audio=null, log=null;
  try {
   var flags=BindingFlags.Instance|BindingFlags.NonPublic;
   var paths=Activator.CreateInstance(assembly.GetType("LSImmersiveLife.LSIMMERSIVEPATH"),flags,null,new object[]{temp},null);
   log=Activator.CreateInstance(assembly.GetType("LSImmersiveLife.LSImmersiveLog"),flags,null,new object[]{data,false},null);
   audio=Activator.CreateInstance(assembly.GetType("LSImmersiveLife.LSPDAudioDispatch"),flags,null,new object[]{paths,log,null,null},null);
   Call(audio,"SetActive",true);
   string id="lsimmersivelife.police.dispatch.received";
   if(Call(audio,"Report",id,"occurrence-1","offered","ramming",null).ToString()!="Queued")throw new Exception("Ramming fallback not queued");
   if(Call(audio,"Report",id,"occurrence-1","offered","ramming",null).ToString()!="Duplicate")throw new Exception("Repeated transition not suppressed");
   var queue=(System.Collections.IList)audio.GetType().GetField("_pending",flags).GetValue(audio);
   string kind=(string)queue[0].GetType().GetField("Kind",flags|BindingFlags.Public).GetValue(queue[0]);
   if(kind!="reckless_driver")throw new Exception("Wrong recorded meaning: "+kind);
   if(Call(audio,"Report","lsimmersivelife.police.scene.activity_confirmed","occurrence-1","observed-activity","ramming",null).ToString()!="Queued")throw new Exception("Reused recording unavailable");
   Call(audio,"CancelScope","occurrence-1");
   if(Call(audio,"Report",id,"occurrence-1","late-stage","ramming",null).ToString()!="ClosedScope")throw new Exception("Closed occurrence accepted audio");
  } finally {
   if(audio is IDisposable disposable)disposable.Dispose();
   if(log!=null)Call(log,"EndSession","Dispatch expansion tests");
   Directory.Delete(temp,true);
  }
 }
}
