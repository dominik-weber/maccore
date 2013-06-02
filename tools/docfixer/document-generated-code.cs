//
// This program takes the API definition from the build and
// uses it to generate the documentation for the auto-generated
// code.   
//
// Unlike the other tools, the documentation generated by this tool
// is based on the knowledge from the API contract file that is
// erased in the compilation process (like the link between events
// and their ObjC delegate classes).
//
// Async Todo:
//    For Async/ResultTypeName classes, which are produced by the generator.cs, we should automatically generate:
//       * Remarks (no summary, since that should be added manually, because we merge that elsewhere)
//       * Constructor docs, and parameter docs taken from the source parameters of the method
//       * Descriptions of the generated properties
//
// Copyright 2012, 2013 Xamarin Inc
//
using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Xml.Linq;
using System.Linq;
using System.Xml.XPath;
using System.Xml;
using System.Text;

#if MONOMAC
using MonoMac.Foundation;
#else
using MonoTouch.Foundation;
#endif
using macdoc;

class DocumentGeneratedCode {
#if MONOMAC
	Type nso = typeof (MonoMac.Foundation.NSObject);
	const string ns = "MonoMac";
	const string docBase = "/Developer/Documentation/DocSets/com.apple.adc.documentation.AppleSnowLeopard.CoreReference.docset";
#else
	Type nso = typeof (MonoTouch.Foundation.NSObject);
	const string ns = "MonoTouch";
	const string docBase = "/Library/Developer/Shared/Documentation/DocSets/com.apple.adc.documentation.AppleiOS5_0.iOSLibrary.docset";
#endif

	static void Help ()
	{
		Console.WriteLine ("Usage is: document-generated-code [--appledocs] temp.dll path-to-documentation");
	}

	static string assembly_dir;
	static Assembly assembly;
	static bool mergeAppledocs;
	static AppleDocMerger docGenerator;

	static Dictionary<Type,XDocument> docs = new Dictionary<Type,XDocument> ();

	static string GetPath (string ns, string typeName, bool notification = false)
	{
		return String.Format ("{0}/{1}/{2}{3}.xml", assembly_dir, ns, typeName, notification ? "+Notifications" : "");
	}
	
	static string GetMdocPath (Type t, bool notification = false)
	{
		var ns = t.Namespace;
		var typeName = t.FullName.Substring (ns.Length+1);
		if (ns == "MonoTouch.Foundation"){
			if (typeName == "NSString2")
				typeName = "NSString";
			if (typeName == "NSObject2")
				typeName = "NSObject";
		}

		return GetPath (ns, typeName, notification);
	}
	
	static XDocument GetDoc (Type t, bool notification = false)
	{
		if (notification == false && docs.ContainsKey (t))
			return docs [t];
		
		string xmldocpath = GetMdocPath (t, notification);
		
		if (!File.Exists (xmldocpath)) {
			Console.WriteLine ("Document missing for type: {0} (File missing={1}), must run update-docs", t.FullName, xmldocpath);
			return null;
		}
		
		XDocument xmldoc;
		try {
			using (var f = File.OpenText (xmldocpath))
				xmldoc = XDocument.Load (f);
			if (notification == false)
				docs [t] = xmldoc;
		} catch {
			Console.WriteLine ("Failure while loading {0}", xmldocpath);
			return null;
		}

		return xmldoc;
	}

	static XDocument GetDoc (string full_type, out string path)
	{
		XDocument xmldoc;
		int lastd = full_type.LastIndexOf (".");
		string ns = full_type.Substring (0, lastd);
		string type = full_type.Substring (lastd+1);

		path = GetPath (ns, type);
		try {
			using (var f = File.OpenText (path))
				xmldoc = XDocument.Load (f);
		} catch {
			Console.WriteLine ("Failure while loading {0}", path);
			return null;
		}

		return xmldoc;
	}
	

	static void Save (string xmldocpath, XDocument xmldoc)
	{
		var xmlSettings = new XmlWriterSettings (){
			Indent = true,
			Encoding = new UTF8Encoding (false),
			OmitXmlDeclaration = true,
			NewLineChars = Environment.NewLine
		};
		using (var output = File.CreateText (xmldocpath)){
			using (var xmlw = XmlWriter.Create (output, xmlSettings)){
				xmldoc.Save (xmlw);
				output.WriteLine ();
			}
		}
	}
	
	static void SaveDocs ()
	{
		foreach (var t in docs.Keys){
			var xmldocpath = GetMdocPath (t);
			var xmldoc = docs [t];

			Save (xmldocpath, xmldoc);
		}
	}

	static XElement GetXmlNodeForMemberName (Type t, XDocument xdoc, string name)
	{
		var field = xdoc.XPathSelectElement ("Type/Members/Member[@MemberName='" + name + "']");
		if (field == null){
			if (!warnings_up_to_date.ContainsKey (t)){
				Console.WriteLine ("Warning: {0} document is not up-to-date with the latest assembly (could not find Field <Member MemberName='{1}')", t, name);
				warnings_up_to_date [t] = true;
			}
		}
		return field;
	}


	// Due to method overloading, we have multiple matches, match the right one based on the parameter names/types
	static XElement GetAsyncXmlNode (Type t, XDocument xdoc, string name, XElement referenceNode)
	{
		var methods = xdoc.XPathSelectElements ("Type/Members/Member[@MemberName='" + name + "']");

		// The parameters must be shared as far as the async node needs
		foreach (var asyncMethod in methods){
			bool fail = false;
			foreach (var apar in asyncMethod.XPathSelectElements ("Parameters/Parameter")){
				var pname = apar.Attribute ("Name").Value;
				var ptype = apar.Attribute ("Type").Value;


				var expr = String.Format ("Parameters/Parameter[@Type='{0}' and @Name='{1}']", ptype, pname);
				
				if (referenceNode.XPathSelectElement (expr) == null){
					fail = true;
					break;
				}
			}
			if (!fail)
				return asyncMethod;
		}
		
		if (!warnings_up_to_date.ContainsKey (t)){
			Console.WriteLine ("Warning: {0} document is not up-to-date with the latest assembly (could not find Field <Member MemberName='{1}')", t, name);
			warnings_up_to_date [t] = true;
		}
		return null;
	}

	static XElement GetXmlNodeFromExport (Type t, XDocument xdoc, string selector)
	{
		return (from m in xdoc.XPathSelectElements ("Type/Members/Member")
			let a = m.XPathSelectElement ("Attributes/Attribute/AttributeName")
			where a != null && a.Value.IndexOf ("MonoTouch.Foundation.Export(\"" + selector + "\"") != -1
			select m).FirstOrDefault ();
	}

	//
	// Handles fields, but perhaps this is better done in DocFixer to pull the definitions
	// from the docs?
	//
	public static void ProcessField (Type t, XDocument xdoc, PropertyInfo pi, bool isNotification)
	{
		var fieldAttr = pi.GetCustomAttributes (typeof (FieldAttribute), true);
		if (fieldAttr.Length == 0)
			return;
		
		var export = ((FieldAttribute) fieldAttr [0]).SymbolName;

		var field = GetXmlNodeForMemberName (t, xdoc, pi.Name);
		if (field == null)
			return;
		
		var returnType = field.XPathSelectElement ("ReturnValue/ReturnType");
		var summary = field.XPathSelectElement ("Docs/summary");
		var remarks = field.XPathSelectElement ("Docs/remarks");
		var example = field.XPathSelectElement ("Docs/remarks/example");
		if (isNotification || (returnType.Value.EndsWith (".Foundation.NSString") && export.EndsWith ("Notification"))){
			if (mergeAppledocs){
				var mdoc = docGenerator.GetAppleMemberDocs (ToCecilType (t), export);
				if (mdoc == null){
					Console.WriteLine ("Failed to load docs for {0} - {1}", t.Name, export);
					return;
				}

				var section = docGenerator.ExtractSection (mdoc);

				//
				// Make this pretty, the first paragraph we turn into the summary,
				// the rest we put in the remarks section
				//
				summary.RemoveAll ();
				summary.Add (section);

				var skipOne = summary.Nodes ().Skip (2).ToArray ();
				remarks.RemoveAll ();
				remarks.Add (skipOne);
				foreach (var n in skipOne)
					n.Remove ();
				if (example != null)
					remarks.Add (example);
			}
		} else {
			var value = field.XPathSelectElement ("Docs/value");
			if (value != null && value.Value == "To be added.")
				value.RemoveAll ();

			//var since = pi.GetCustomAttributes (typeof (SinceAttribute), true);
			//if (since.Length != 0 && pi.PropertyType.IsClass) {
				// TODO: Could format the since value into the text
			//	value.Value = "Value will be null when the constant is not available";
			//}

			if (summary.Value == "To be added."){
				summary.RemoveAll ();
				summary.Value = "Represents the value associated with the constant " + export;
			}
		}
	}

	//
	// Handles notifications
	//
	static Dictionary<Type,List<Type>> event_args_to_notification_uses = new Dictionary<Type,List<Type>> ();
	static Dictionary<Type,bool> warnings_up_to_date = new Dictionary<Type, bool> ();
	static List<Type> nested_types = new List<Type> ();
	
	public static void ProcessNotification (Type t, XDocument xdoc, PropertyInfo pi)
	{
		var notification = pi.GetCustomAttributes (typeof (NotificationAttribute), true);
		if (notification.Length == 0)
			return;
		
		var notification_event_args = ((NotificationAttribute) notification [0]).Type;
		
		var field = xdoc.XPathSelectElement ("Type/Members/Member[@MemberName='" + pi.Name + "']");
		if (field == null){
			if (!warnings_up_to_date.ContainsKey (t)){
				Console.WriteLine ("WARNING: {0} document is not up-to-date with the latest assembly", t);
				warnings_up_to_date [t] = true;
			}
			return;
		}
		var name = pi.Name;
		var mname = name.EndsWith ("Notification") ? name.Substring (0, name.Length-("Notification".Length)) : name;
		
		var returnType = field.XPathSelectElement ("ReturnValue/ReturnType");
		var summary = field.XPathSelectElement ("Docs/summary");
		var remarks = field.XPathSelectElement ("Docs/remarks");
		var example = field.XPathSelectElement ("Docs/remarks/example");

		var body = new StringBuilder ("    Console.WriteLine (\"Notification: {0}\", args.Notification);");
	
		if (notification_event_args != null){
			body.Append ("\n");
			foreach (var p in notification_event_args.GetProperties ()){
				body.AppendFormat ("\n    Console.WriteLine (\"{0}\", args.{0});", p.Name);
			}
		}

		var evengArgsType = DocumentNotificationNestedType (t, pi, body.ToString ());

		remarks.RemoveAll ();
		remarks.Add (XElement.Parse ("<para id='tool-remark'>This constant can be used with the <see cref=\"T:MonoTouch.Foundation.NSNotificationCenter\"/> to register a listener for this notification.   This is an NSString instead of a string, because these values can be used as tokens in some native libraries instead of being used purely for their actual string content.    The 'notification' parameter to the callback contains extra information that is specific to the notification type.</para>"));
		remarks.Add (XElement.Parse (String.Format ("<para id='tool-remark'>If you want to subscribe to this notification, you can use the convenience <see cref='T:{0}+Notifications'/>.<see cref='M:{0}+Notifications.Observe{1}'/> method which offers strongly typed access to the parameters of the notification.</para>", t.Name, name)));
		remarks.Add (XElement.Parse ("<para>The following example shows how to use the strongly typed Notifications class, to take the guesswork out of the available properties in the notification:</para>"));
		remarks.Add (XElement.Parse (String.Format ("<example><code lang=\"c#\">\n" +
							    "//\n// Lambda style\n//\n\n// listening\n" +
							    "notification = {0}.Notifications.Observe{1} ((sender, args) => {{\n    /* Access strongly typed args */\n{2}\n}});\n\n" +
							    "// To stop listening:\n" +
							    "notification.Dispose ();\n\n" +
							    "//\n// Method style\n//\nNSObject notification;\n" +
							    "void Callback (object sender, {3} args)\n"+
							    "{{\n    // Access strongly typed args\n{2}\n}}\n\n" +
							    "void Setup ()\n{{\n" +
							    "    notification = {0}.Notifications.Observe{1} (Callback);\n}}\n\n" +
							    "void Teardown ()\n{{\n" +
							    "    notification.Dispose ();\n}}</code></example>", t.Name, mname, body, evengArgsType)));
		remarks.Add (XElement.Parse ("<para>The following example shows how to use the notification with the DefaultCenter API:</para>"));
		remarks.Add (XElement.Parse (String.Format ("<example><code lang=\"c#\">\n" +
						      "// Lambda style\n" +
						      "NSNotificationCenter.DefaultCenter.AddObserver (\n        {0}.{1}, (notification) => {{Console.WriteLine (\"Received the notification {0}\", notification); }}\n\n\n" +
						      "// Method style\n" +
						      "void Callback (NSNotification notification)\n{{\n" +
						      "    Console.WriteLine (\"Received a notification {0}\", notification);\n}}\n\n" +
						      "void Setup ()\n{{\n" +
						      "    NSNotificationCenter.DefaultCenter.AddObserver ({0}.{1}, Callback);\n}}\n</code></example>", t.Name, name)));
		

		// Keep track of the uses, so we can list all of the observers.
		if (notification_event_args != null){
			List<Type> list;
			if (!event_args_to_notification_uses.ContainsKey (notification_event_args))
				list = new List<Type> ();
			else
				list = event_args_to_notification_uses [notification_event_args];
			list.Add (notification_event_args);
			event_args_to_notification_uses [notification_event_args] = list;
		}
	}

	public static string DocumentNotificationNestedType (Type t, PropertyInfo pi, string body)
	{
		string handlerType = null;
		var class_doc = GetDoc (t, notification: true);

		if (class_doc == null){
			Console.WriteLine ("Error, can not find Notification class for type {0}", t);
			Environment.Exit (1);
		}
		var class_summary = class_doc.XPathSelectElement ("Type/Docs/summary");
		var class_remarks = class_doc.XPathSelectElement ("Type/Docs/remarks");

		class_summary.RemoveAll ();
		class_summary.Add (XElement.Parse ("<para>Notification posted by the <see cref =\"T:" + t.FullName + "\"/> class.</para>"));
		class_remarks.RemoveAll ();
		class_remarks.Add (XElement.Parse ("<para>This is a static class which contains various helper methods that allow developers to observe events posted " +
						   "in the iOS notification hub (<see cref=\"T:MonoTouch.Foundation.NSNotificationCenter\"/>).</para>"));
		class_remarks.Add (XElement.Parse ("<para>The methods defined in this class post events invoke the provided method or lambda with a " +
						   "<see cref=\"T:MonoTouch.Foundation.NSNotificationEventArgs\"/> parameter which contains strongly typed properties for the notification arguments.</para>"));

		var notifications = from prop in t.GetProperties ()
			let propName = prop.Name
			where propName == pi.Name
			let fieldAttrs = prop.GetCustomAttributes (typeof (FieldAttribute), true)
			where fieldAttrs.Length > 0 && prop.GetCustomAttributes (typeof (NotificationAttribute), true).Length > 0
			let propLen = propName.Length
			let convertedName = propName.EndsWith ("Notification") ? propName.Substring (0, propLen-("Notification".Length)) : propName
			select new Tuple<string,string> (convertedName, ((FieldAttribute) fieldAttrs [0]).SymbolName) ;

		// So the code below actually only executes once.
		if (notifications.Count() > 1){
			Console.WriteLine ("WHOA!   DocumentNotificationNestedType got more than 1 notification");
		}
		
		foreach (var notification in notifications){
			var mname = "Observe" + notification.Item1;
			var method = class_doc.XPathSelectElement ("Type/Members/Member[@MemberName='" + mname + "']");

			var handler = method.XPathSelectElement ("Docs/param");

			handlerType = (string) method.XPathSelectElement ("Parameters/Parameter").Attribute ("Type");
			if (handlerType.StartsWith ("System.EventHandler<"))
				handlerType = handlerType.Substring (20, handlerType.Length-21);

			// Turn System.EventHandler<Foo> into EventHandler<Foo>, looks prettier
			if (handlerType.StartsWith ("System."))
				handlerType = handlerType.Substring (7);
			var summary = method.XPathSelectElement ("Docs/summary");
			var remarks = method.XPathSelectElement ("Docs/remarks");
			var returns = method.XPathSelectElement ("Docs/returns");
			if (handler == null)
				Console.WriteLine ("Looking for {0}, and this is the class\n{1}", notification.Item1, class_doc);
			handler.Value = "Method to invoke when the notification is posted.";
			summary.Value = "Registers a method to be notified when the " + notification.Item2 + " notification is posted.";
			returns.RemoveAll ();
			returns.Add (XElement.Parse ("<para>The returned NSObject represents the registered notification.   Either call Dispose on the object to stop receiving notifications, or pass it to <see cref=\"M:MonoTouch.Foundation.NSNotificationCenter.RemoveObserver\"/></para>"));
			remarks.RemoveAll ();
			remarks.Add (XElement.Parse ("<para>The following example shows how you can use this method in your code</para>"));

			remarks.Add (XElement.Parse (String.Format ("<example><code lang=\"c#\">\n" +
								    "//\n// Lambda style\n//\n\n// listening\n" +
								    "notification = {0}.Notifications.{1} ((sender, args) => {{\n    /* Access strongly typed args */\n{2}\n}});\n\n" +
								    "// To stop listening:\n" +
								    "notification.Dispose ();\n\n" +
								    "//\n//Method style\n//\nNSObject notification;\n" +
								    "void Callback (object sender, {3} args)\n"+
								    "{{\n    // Access strongly typed args\n{2}\n}}\n\n" +
								    "void Setup ()\n{{\n" +
								    "    notification = {0}.Notifications.{1} (Callback);\n}}\n\n" +
								    "void Teardown ()\n{{\n" +
								    "    notification.Dispose ();\n}}</code></example>", t.Name, mname, body, handlerType)));
		
		}
		Save (GetMdocPath (t, true), class_doc);
		return handlerType;
	}

	public static void PopulateEvents (XDocument xmldoc, BaseTypeAttribute bta, Type t)
	{
		for (int i = 0; i < bta.Events.Length; i++){
			var delType = bta.Events [i];
			var evtName = bta.Delegates [i];
			foreach (var mi in delType.GatherMethods ()){
				var method = xmldoc.XPathSelectElement ("Type/Members/Member[@MemberName='" + mi.Name + "']");
				if (method == null){
					Console.WriteLine ("Documentation not up to date for {0}, member {1} was not found", delType, mi.Name);
					continue;
				}
				var summary = method.XPathSelectElement ("Docs/summary");
				var remarks = method.XPathSelectElement ("Docs/remarks");
				var returnType = method.XPathSelectElement ("ReturnValue/ReturnType");

				if (mi.ReturnType == typeof (void)){
					summary.Value = "Event raised by the object.";
					remarks.Value = "If you assign a value to this event, this will reset the value for the " + evtName + " property to an internal handler that maps delegates to events.";
				} else {
					summary.Value = "Delegate invoked by the object to get a value.";
					remarks.Value = "You assign a function, delegate or anonymous method to this property to return a value to the object.   If you assign a value to this property, it this will reset the value for the " + evtName + " property to an internal handler that maps delegates to events.";
				}
			}
		}
	}

	//
	// This prepares a node that contains text, like this:
	// <foo>To be added.</foo>
	//
	// to wrap the text in a para.
	// <foo><para>To be added.</para></foo>
	//
	static void PrepareNakedNode (XElement element, bool addStub = true)
	{
		if (element.HasElements)
			return;
		var val = element.Value;
		if (addStub && val == "To be added.")
			val = "(More documentation for this node is coming)";
		
		element.Value = "";
		element.Add (XElement.Parse ("<para>" + val + "</para>"));
	}
	
	
	public static void AnnotateNullAllowedPropertyValue (Type t, XDocument xdoc, PropertyInfo pi)
	{
		var field = GetXmlNodeForMemberName (t, xdoc, pi.Name);
		if (field == null)
			return;
		var value = field.XPathSelectElement ("Docs/value");
		var toolgen_null_annotations = value.XPathSelectElement ("para[@tool='nullallowed']");
		if (toolgen_null_annotations == null)
			PrepareNakedNode (value);
		else
			toolgen_null_annotations.Remove ();

		value.Add (XElement.Parse ("<para tool='nullallowed'>This value can be <see langword=\"null\"/>.</para>"));
	}

	//
	// Copy the remarks to an async node, preserving any content on the target if needed, to do that
	// we copy elements and flag them as "copied=true"
	//
	public static void CopyRemarksToAsync (XElement source, XElement target, string asyncName)
	{
		const string xpath = "Docs/remarks";
		var tnode = target.XPathSelectElement (xpath);
		var node = source.XPathSelectElement (xpath);

		// Remove previously copied stuff.
		foreach (var deletableElement in tnode.XPathSelectElements ("//*[@copied='true' or @generated='true']")){
			deletableElement.Remove ();
		}
		if (tnode.Value == "To be added."){
			tnode.Value = "";
		}

		// Wrap simple text
		if (tnode.Elements ().Count () == 0 && tnode.Value != ""){
			var val = tnode.Value;
			tnode.Value = "";
			tnode.Add (XElement.Parse ("<para>" + val + "</para>"));
		}
		
		tnode.Add (XElement.Parse ("<para copied='true'>The " + asyncName + " method is suitable to be used with C# async by returning control to the caller with a Task representing the operation.</para>"));

		// Add simple text from the original
		if (node.Elements ().Count () == 0 && node.Value != "")
			tnode.Add (XElement.Parse ("<para copied='true'>" + node.Value + "</para>"));
		else {
			foreach (var e in node.Elements ()){
				var copy = new XElement (e);
				e.SetAttributeValue ("copied", "true");
			}
		}
	}

	// If the contents of the node has any of our "improve" annotations, it means we have not
	// manually edited it, so we want to inject some standard text.
	static bool HasImproveAnnotation (XElement node)
	{
		var nodes = node.XPathSelectElements ("//*[@class='improve-task-t-return-type-description' or @class='improve']");
		return nodes != null && nodes.Count () > 0;
	}
	
	static Dictionary<string,int> async_result_types = new Dictionary<string,int>();
	static Dictionary<string,int> hot_summary = new Dictionary<string,int>();
	static Dictionary<string,List<string>> resultTypeNameUses = new Dictionary<string,List<string>> ();
	
	//
	// Copy summary (maybe later we modify it?)
	// Copy parameters
	//
	// This need some smarts to copy the description of the Task<T> result
	// 
	public static void UpdateAsyncDocsFromMaster (AsyncAttribute aa, Type t, string name, string asyncName, XElement node, XElement nodeAsync)
	{
		string fullMethodName = t.FullName + "." + asyncName;
		// Copy the summary information
		var tsummary = nodeAsync.XPathSelectElement ("Docs/summary");
		var summary = node.XPathSelectElement ("Docs/summary");

		if (summary.Value == "To be added."){
			if (hot_summary.ContainsKey (fullMethodName))
				hot_summary [fullMethodName]++;
			else
				hot_summary [fullMethodName] = 1;
		}
		
		tsummary.Value = summary.Value;

		// Copy remarks
		CopyRemarksToAsync (node, nodeAsync, asyncName);

		// Handle Return values
		var lastParameter = node.XPathSelectElement ("Parameters/Parameter[last()]");
		var returns = nodeAsync.XPathSelectElement ("Docs/returns");
		var retType = nodeAsync.XPathSelectElement ("ReturnValue/ReturnType");
		if (retType == null){
			Console.WriteLine ("FAIL: {0}", nodeAsync);
			Console.WriteLine (nodeAsync);
			throw new Exception();
		}
		if (retType.Value == "System.Threading.Tasks.Task"){
			returns.Value = "A task that represents the asynchronous " + name + " operation";
		} else if (returns.Value == "To be added." || HasImproveAnnotation (returns)) {
			var task_result_type = lastParameter.Attribute ("Type").Value;
			// We have a Task<T>
			returns.RemoveAll ();
			if (task_result_type.StartsWith ("System.Action<")){
				Console.WriteLine ("Improvement for Task<T> in {0}", fullMethodName);
				var tresult_type = task_result_type.Substring (14).Trim ('>');
				string standard_boring_text = "<para class='improve-task-t-return-type-description'>A task that represents the asynchronous " + name + " operation.  The value of the TResult parameter is of type " + tresult_type + ".</para>";

				// Should eventually only remove if we have 'To be added.' but my local copy is butchered (cant git reset right now)
				returns.Add (XElement.Parse (standard_boring_text));
			} else {
				// Load the delegate type from the disk, and pull the docs from it
				var tresult_type = retType.Value.Substring (28).Trim ('>');
				// If we have a [Async (ResultTypeName="xxx")]
				if (aa.ResultTypeName != null){
					//
					// We know we need to look up the information from the type, get the full type name from the tresult_type
					//
					string path;
					var tresultDoc = GetDoc (tresult_type, out path);
					var tresultSummary = tresultDoc.XPathSelectElement ("/Type/Docs/summary").Value;
					if (tresultSummary == "To be added."){
						Console.WriteLine ("Improvement: If you document the delegate for the {0} summary, you will get better async docs for {1}", tresult_type, fullMethodName);
					}
					returns.Add (XElement.Parse ("<para>A task that represents the asynchronous " + name + " operation.   The value of the TResult parameter is of type " + tresult_type + ".  " +  tresultSummary + "</para>"));

					List<string> list;
					if (!resultTypeNameUses.TryGetValue (tresult_type, out list)){
						list = new List<string> ();
						resultTypeNameUses [tresult_type] = list;
					}
					list.Add (fullMethodName);

					var used_by = string.Join (", ", list.Select (x=>"<see cref=\"T:" + x + "\"/>").ToArray ());
					var trem = tresultDoc.XPathSelectElement ("/Type/Docs/remarks");
					trem.RemoveAll ();
					trem.Add (XElement.Parse ("<para>This class holds the return values from the asynchronous method" + (list.Count > 1 ? "s " : " ") + used_by + ".</para>"));
					//
					// Also document the Async [ResultTypeName]
					//
					var ctor = tresultDoc.XPathSelectElement ("/Type/Members/Member[@MemberName='.ctor']");
					ctor.XPathSelectElement ("Docs/summary").Value = "Constructs an instance of " + tresult_type;
					ctor.XPathSelectElement ("Docs/remarks").Value = "";
					foreach (var par in ctor.XPathSelectElements ("Docs/param")){
						par.Value = "Result value from the async operation";
					}
					
					Console.WriteLine ("Saving {0} for {1}", path, fullMethodName);
					tresultDoc.Save (path);
				} else {
					string path;
					var delegateDoc = GetDoc (task_result_type, out path);
					if (delegateDoc == null){
						Console.WriteLine ("Error: Failed to load the documentation for a referenced delegate: {0}", task_result_type);
						return;
					}
					returns.Add (XElement.Parse ("<para>A task that represents the asynchronous " + name + " operation.   The value of the TResult parameter is a " + task_result_type + ".</para>"));
				}
			}

			if (async_result_types.ContainsKey (task_result_type))
				async_result_types [task_result_type]++;
			else
				async_result_types [task_result_type] = 1;
			//Console.WriteLine ("Method {0}.{1}'s Async really could use an explanation of the return type {2}", t, name, retType.Value);
		}

		//
		// Copy parameter docs
		//
		foreach (var par in nodeAsync.XPathSelectElements ("Docs/param")){
			var parName = par.Attribute ("name").Value;
			var expr = "Docs/param[@name='" + parName + "']";

			try {
				var original = node.XPathSelectElements (expr).First ();
				
				par.Value = original.Value;
			} catch {
				Console.WriteLine ("Failed to lookup with {0} and {1} and {2}", expr, node, nodeAsync);
			}
		}
		
	}
	
	public static void ProcessNSO (Type t, BaseTypeAttribute bta)
	{
		var xmldoc = GetDoc (t);
		if (xmldoc == null){
			Console.WriteLine ("Can not find docs for {0}", t);
			return;
		}

		foreach (var pi in t.GatherProperties ()){
			object [] attrs;
			var kbd = false;
			if (pi.GetCustomAttributes (typeof (InternalAttribute), true).Length > 0)
				continue;

			if (pi.GetCustomAttributes (typeof (FieldAttribute), true).Length > 0){
				bool is_notification = pi.GetCustomAttributes (typeof (NotificationAttribute), true).Length != 0;
				ProcessField (t, xmldoc, pi, is_notification);

				if (is_notification)
					ProcessNotification (t, xmldoc, pi);
				continue;
			}
			if (pi.GetCustomAttributes (typeof (NullAllowedAttribute), true).Length > 0){
				AnnotateNullAllowedPropertyValue (t, xmldoc, pi);

				//
				// Propagate the [NullAllowed] from the WeakXXX to XXX
				//
				if (pi.Name.StartsWith ("Weak")){
					var npi = t.GetProperty (pi.Name.Substring (4));

					// Validate that the other property is actually a wrapper around this one.
					if (npi != null && npi.GetCustomAttributes (typeof (WrapAttribute), true).Length > 0){
						if (npi != null)
							AnnotateNullAllowedPropertyValue (t, xmldoc, npi);
					} else
						Console.WriteLine ("Did not find matching {0}", pi.Name);
					
				}
			}
			if (pi.GetCustomAttributes (typeof (ThreadSafeAttribute), true).Length > 0){
				var field = GetXmlNodeForMemberName (t, xmldoc, pi.Name);
				if (field == null)
					return;
				var remarks = field.XPathSelectElement ("Docs/remarks");
				if (remarks != null)
					AddThreadRemark (remarks, "This");
			}
		}
		foreach (var mi in t.GatherMethods ()){
			//
			// Since it is a pain to go from a MethodInfo into an ECMA XML signature name
			// we will lookup the method by the [Export] attribute
			//

			try {
				if (mi.GetCustomAttributes (typeof (InternalAttribute), true).Length > 0)
					continue;
			} catch (Exception FileNotFoundException){
				Console.WriteLine ("*** Problem loading attributes for {0}.{1} ***", t, mi.Name);
				continue;
			}

			var attrs = mi.GetCustomAttributes (typeof (ExportAttribute), true);
			if (attrs.Length == 0)
				continue;
			var ea = attrs [0] as ExportAttribute;
			var node = GetXmlNodeFromExport (t, xmldoc, ea.Selector);
			if (node == null){
				Console.WriteLine ("Did not find the selector {0} on type {1}", ea.Selector, t);
				continue;
			}
			
			attrs = mi.GetCustomAttributes (typeof (AsyncAttribute), true);
			if (attrs.Length > 0){
				var aa = attrs [0] as AsyncAttribute;
				var methodName = aa.MethodName;
				if (methodName == null)
					methodName = mi.Name + "Async";
				
				var nodeAsync = GetAsyncXmlNode (t, xmldoc, methodName, node);
				if (nodeAsync == null)
					Console.WriteLine ("*** ____ OH NO!  WE HAVE A PROBLEM: The Async Method Referenced is not on the documentation for {0} {1}, I expected {2}", t, mi.Name, methodName);
				else {
					UpdateAsyncDocsFromMaster (aa, t, mi.Name, methodName, node, nodeAsync);
				}				
			}
			
			if (mi.GetCustomAttributes (typeof (ThreadSafeAttribute), true).Length > 0){
				var remarks = node.XPathSelectElement ("Docs/remarks");
				AddThreadRemark (remarks, "This");
			}
			foreach (var parameter in mi.GetParameters ()){
				if (parameter.GetCustomAttributes (typeof (NullAllowedAttribute), true).Length > 0){
					var par = node.XPathSelectElement ("Docs/param[@name='" + parameter.Name + "']");
					if (par == null){
						Console.WriteLine ("Did not find parameter {0} in {1}.{2}\n{3}", parameter.Name, t, mi, node);
						continue;
					}
					var toolgen_null_annotations = par.XPathSelectElement ("para[@tool='nullallowed']");
					if (toolgen_null_annotations == null)
						PrepareNakedNode (par, addStub: false);
					else
						toolgen_null_annotations.Remove ();
					par.Add (XElement.Parse ("<para tool='nullallowed'>This parameter can be <see langword=\"null\"/>.</para>"));
				}
			}
		}
		
		if (bta != null && bta.Events != null){
			PopulateEvents (xmldoc, bta, t);
		}
	}

	static void AddThreadRemark (XElement node, string msg)
	{
		var threadNode =  node.XPathSelectElement ("para[@tool='threads']");
		if (threadNode == null)
			PrepareNakedNode (node);
		else
			threadNode.Remove ();
		node.Add (XElement.Parse ("<para tool='threads'>" + msg + " can be used from a background thread.</para>"));
	}
	
	public static void AnnotateThreadSafeType (Type t)
	{
		var xmldoc = GetDoc (t);
		if (xmldoc == null){
			Console.WriteLine ("Can not find docs for {0}", t);
			return;
		}
		var typeRemarks = xmldoc.XPathSelectElement ("Type/Docs/remarks");
		AddThreadRemark (typeRemarks, "The members of this class");

		var memberRemarks = xmldoc.XPathSelectElements ("Type/Members/Member/Docs/remarks");
		foreach (var mr in memberRemarks)
			AddThreadRemark (mr, "This");
	}
	
	public static int Main (string [] args)
	{
		string dir = null;
		string lib = null;
		var debug = Environment.GetEnvironmentVariable ("DOCFIXER");
		bool debugDoc = false;
		
		for (int i = 0; i < args.Length; i++){
			var arg = args [i];
			if (arg == "-h" || arg == "--help"){
				Help ();
				return 0;
			}
			if (arg == "--appledocs"){
				mergeAppledocs = true;
				continue;
			}
			if (arg == "--debugdoc"){
				debugDoc = true;
				continue;
			}
			
			if (lib == null)
				lib = arg;
			else
				dir = arg;
		}
		
		if (dir == null){
			Help ();
			return 1;
		}
		
		if (File.Exists (Path.Combine (dir, "en"))){
			Console.WriteLine ("The directory does not seem to be the root for documentation (missing `en' directory)");
			return 1;
		}
		assembly_dir = Path.Combine (dir, "en");
		assembly = Assembly.LoadFrom (lib);

		if (mergeAppledocs){
			docGenerator = new AppleDocMerger (new AppleDocMerger.Options {
				DocBase = Path.Combine (docBase, "Contents/Resources/Documents/documentation"),
				DebugDocs = debugDoc,
				MonodocArchive = new MDocDirectoryArchive (assembly_dir),
					Assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly (lib),
					BaseAssemblyNamespace = ns,
					ImportSamples = false
					});
		}

		foreach (Type t in assembly.GetTypes ()){
			if (t.GetCustomAttributes (typeof (ThreadSafeAttribute), true).Length > 0)
				AnnotateThreadSafeType (t);
			
			if (debugDoc && mergeAppledocs){
				string str = docGenerator.GetAppleDocFor (ToCecilType (t));
				if (str == null){
					Console.WriteLine ("Could not find docs for {0}", t);
				}
				
				continue;
			}

			if (debug != null && t.FullName != debug)
				continue;

			var btas = t.GetCustomAttributes (typeof (BaseTypeAttribute), true);
			ProcessNSO (t, btas.Length > 0  ? (BaseTypeAttribute) btas [0] : null);
		}

		foreach (Type notification_event_args in event_args_to_notification_uses.Keys){
			var uses = event_args_to_notification_uses [notification_event_args];

			
		}
		Console.WriteLine ("saving");
		SaveDocs ();

		Console.WriteLine ("Async Result Types");
		foreach (var j in from kv in async_result_types orderby kv.Value descending select kv)
			Console.WriteLine ("   Async Result: {1} {0}", j.Key, j.Value);
		Console.WriteLine ("Hot summaries");
		foreach (var j in from hskv in hot_summary orderby hskv.Value descending select hskv)
			Console.WriteLine ("   Hot Summary: {1} {0}", j.Key, j.Value);
		return 0;
	}
	
	static Mono.Cecil.TypeDefinition ToCecilType (Type t)
	{
		return new Mono.Cecil.TypeDefinition (t.Namespace, t.Name, (Mono.Cecil.TypeAttributes)t.Attributes);
	}
}
