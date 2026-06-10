namespace MMS.Tools.RequestResponseProxyProjectBuilder.Roslyn;

public static class Templates
{
    public const string Request = """
using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;
using System.Reflection;
using MMS.Web.UI;
using MMS.Web.UI.Actions;

namespace <[Namespace]>
{
	/// <summary>
	/// Request proxy for <[ClassName]>
	/// </summary>
	public class <[ClassName]>Proxy : ProxyBase<[Interfaces]>
	{
		<[ForEachEvent]>
		/// <summary>
		/// Add a delegate to this event and it will be registered as an event handler.
		/// Note that it must still be public and marked with the Action attribute.
		/// </summary>
        public event <[EventType]> <[EventName]>;
		</[ForEachEvent]>
		
		/// <summary>
		/// Initialise from the client page that owns the page to be loaded.
		/// </summary>
		public <[ClassName]>Proxy(ClientPage owner)
			: base(typeof(<[ClassName]>))
		{
			this.owner = owner;
			this.control = owner.ControlId;
		}

		<[ForEachCtor]>
		/// <summary>
		/// Show a dialog containing the client page.
		/// </summary>
        public void ShowDialog(string dialogContainerId, string dialogTitle <[ForEachCtorParam]>,<[CtorParamType]> <[CtorParamName]></[ForEachCtorParam]> )
        {
			var __service = MedDBase.Global.Application.CreateFileService();
			var __file = __service.FindXmlFile("Dialogs\\<[FqClassName]>");
			string __className = dialogContainerId;
			if( __file != null )
			{
				XmlDocument xml = new XmlDocument();
				using (var stream = __service.GetFileStream(__file))
				{
					xml.Load(stream);
				}
				__className = xml.DocumentElement["Style"].InnerText;
			}

			MMS.Web.QueryStringBuilder __url = new MMS.Web.QueryStringBuilder();
			__url.SendNulls = true;
			AddWorkflow(__url);
			<[ForEachCtorParam]>__url.Append("<[CtorParamName]>", <[CtorParamName]>);</[ForEachCtorParam]>
                                
			LoadChildDialog(
					__className,
					dialogContainerId,
                    "<[NamespaceSlashed]>/<[ClassName]>"+__url.ToString(),
                    dialogTitle
                );
        
			<[ForEachEvent]>
            if (<[EventName]> != null)
            {
				ProcessEventDelegate(dialogContainerId,<[EventName]>,"<[EventName]>");
            }
            </[ForEachEvent]>
        }
        
		/// <summary>
		/// Show the client page in a container on the owner page.
		/// </summary>
		public void Show(string clientPageContainerId, string clientPageId <[ForEachCtorParam]>,<[CtorParamType]> <[CtorParamName]></[ForEachCtorParam]> )
		{
			MMS.Web.QueryStringBuilder __url = new MMS.Web.QueryStringBuilder();
			__url.SendNulls = true;
			AddWorkflow(__url);
			<[ForEachCtorParam]>__url.Append("<[CtorParamName]>", <[CtorParamName]>);</[ForEachCtorParam]>
        
            this.owner.Response.AddLateAction(
                new LoadChild(
					"",
					clientPageContainerId,
					clientPageId,
                    "<[NamespaceSlashed]>/<[ClassName]>"+__url.ToString()
                ));
        
			<[ForEachEvent]>
            if (<[EventName]> != null)
            {
				ProcessEventDelegate(clientPageId,<[EventName]>,"<[EventName]>");
            }
            </[ForEachEvent]>
		}        
        
		/// <summary>
		/// Link to the page for inserting directly into &lt;a&gt; tags
		/// </summary>
        public string Link(<[ForEachCtorParam]><[CtorParamType]> <[CtorParamName]><[CommaIfNotLast]></[ForEachCtorParam]>)
        {
			return "nmp.aspx?cp="+
			"<[Namespace]>".Substring("MedDBase.Pages.".Length).Replace('.','/')+"/<[ClassName]>"
			<[ForEachCtorParam]>+"&<[CtorParamName]>="+ToStringForLink(<[CtorParamName]>)</[ForEachCtorParam]>;
        }
        
		/// <summary>
		/// Link to the page for inserting directly into &lt;a&gt; tags
		/// </summary>
        public string LinkEmbdedded(<[ForEachCtorParam]><[CtorParamType]> <[CtorParamName]><[CommaIfNotLast]></[ForEachCtorParam]>)
        {
			return "nem.aspx?cp="+
			"<[Namespace]>".Substring("MedDBase.Pages.".Length).Replace('.','/')+"/<[ClassName]>"
			<[ForEachCtorParam]>+"&<[CtorParamName]>="+ToStringForLink(<[CtorParamName]>)</[ForEachCtorParam]>;
        }
        
		/// <summary>
		/// Redirect
		/// </summary>
		public void Redirect(<[ForEachCtorParam]><[CtorParamType]> <[CtorParamName]><[CommaIfNotLast]></[ForEachCtorParam]>)
		{
			RedirectInternal(PageTarget.Self, "nmp.aspx" <[ForEachCtorParam]>,<[CtorParamName]> </[ForEachCtorParam]>);
		}
		
		/// <summary>
		/// Redirect
		/// </summary>
		public void Redirect(PageTarget target<[ForEachCtorParam]>,<[CtorParamType]> <[CtorParamName]></[ForEachCtorParam]>)
		{
			RedirectInternal(target, "nmp.aspx" <[ForEachCtorParam]>,<[CtorParamName]></[ForEachCtorParam]>);
		}
		
		/// <summary>
		/// Redirect
		/// </summary>
		public void RedirectEmbedded(<[ForEachCtorParam]><[CtorParamType]> <[CtorParamName]><[CommaIfNotLast]></[ForEachCtorParam]>)
		{
			RedirectInternal(PageTarget.Self, "nem.aspx"<[ForEachCtorParam]>, <[CtorParamName]></[ForEachCtorParam]>);
		}
		
		/// <summary>
		/// Redirect
		/// </summary>
		public void RedirectEmbedded(PageTarget target<[ForEachCtorParam]>,<[CtorParamType]> <[CtorParamName]></[ForEachCtorParam]>)
		{
			RedirectInternal(target, "nem.aspx"<[ForEachCtorParam]>, <[CtorParamName]></[ForEachCtorParam]>);
		}
		
		/// <summary>
		/// Redirect
		/// </summary>
		private void RedirectInternal(PageTarget target, string aspxPage <[ForEachCtorParam]>,<[CtorParamType]> <[CtorParamName]></[ForEachCtorParam]>)
		{
			MMS.Web.QueryStringBuilder __url = new MMS.Web.QueryStringBuilder();
			__url.SendNulls = true;
			AddWorkflow(__url);
			<[ForEachCtorParam]>__url.Append("<[CtorParamName]>", <[CtorParamName]>);</[ForEachCtorParam]>
			
			string __urlString = __url.ToString();
			if (__urlString.Length > 0)
			{
				__urlString = __urlString.Substring(1);

				if( target.Target=="_self" )
				{
					this.owner.Response.Actions.RedirectUrl(
						aspxPage+"?cp=<[NamespaceSlashed]>/<[ClassName]>&" + __urlString
					);
				}
				else
				{
					this.owner.Response.Actions.RedirectUrl(
						aspxPage+"?cp=<[NamespaceSlashed]>/<[ClassName]>&" + __urlString,
						target.Target
					);
				}
			}
			else
			{
				this.owner.Response.Actions.RedirectUrl(
					aspxPage+"?cp=<[NamespaceSlashed]>/<[ClassName]>",
					target.Target
				);
			}
		}        
		
        
		</[ForEachCtor]>
		
		<[ForEachAction]>
		/// <summary>
		/// Invoke the '<[ActionName]>' action on the specified page
		/// </summary>
		/// <param name="late">if set to <c>true</c> the action will be invoked late.</param>
		/// <param name="clientPageId">The client page id of the list pane.</param>
		<[ForEachActionParam]>/// <param name="<[ActionParamName]>"><[ActionParamName]></param>
		</[ForEachActionParam]>
		public void <[ActionName]> (bool late, string clientPageId<[ForEachActionParam]>,<[ActionParamType]> <[ActionParamName]> </[ForEachActionParam]>)
		{
			StringBuilder scriptBuilder = new StringBuilder();
			scriptBuilder.AppendFormat("Server.{0}.<[ActionName]>(", clientPageId);
			<[ForEachActionParam]>scriptBuilder.Append(MMS.MMSConvert.ToJsStringSingleQuote(<[ActionParamName]>.ToString())+"<[CommaIfNotLast]>");
			</[ForEachActionParam]>scriptBuilder.Append(");");
			MMS.Web.UI.Actions.Helper helper = owner.Response.Actions;
			if (late)
			{
				helper = owner.Response.LateActions;
			}
			helper.RunScript(scriptBuilder.ToString());
		}
				
		</[ForEachAction]>
	}
}

""";

    public const string Response = """
using System;
using System.Collections.Generic;
using System.Text;
using MMS.Web.UI;
using MMS.Web.UI.Attributes;
using MMS.Web.UI.Response;

namespace <[Namespace]>
{
	/// <summary>
	/// Response proxy for <[ClassName]>
	/// </summary>
	public class <[ClassName]>ResponseProxy
	{
		ClientPage owner;
	
		/// <summary>
		/// Initialise from the client page that owns the page being loaded.
		/// </summary>
		public <[ClassName]>ResponseProxy(ClientPage owner)
		{
			this.owner = owner;
		}

		/// <summary>
		/// Register delegates with events so that invoking the event will add a client
		/// page event to the response.
		/// </summary>
		public void RegisterEvents()
		{
			<[ForEachEvent]>
			((<[FqClassName]>)this.owner).<[EventName]> += new <[EventType]>(<[EventName]>);
			</[ForEachEvent]>
		}
	
		<[ForEachEvent]>
		/// <summary>
		/// This delegate is registered with the corresponding event in RegisterEvents so
		/// that invoking that event adds an event to the response.
		/// </summary>
		public void <[EventName]>(<[ForEachEventParam]><[EventParamType]> <[EventParamName]><[CommaIfNotLast]></[ForEachEventParam]>)
		{
			this.owner.Response.AddEvent( "<[EventName]>" <[ForEachEventParam]>,<[EventParamName]></[ForEachEventParam]> );
		}
		</[ForEachEvent]>
	}
}

""";
}
