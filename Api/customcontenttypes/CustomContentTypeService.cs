using Api.Database;
using System.Threading.Tasks;
using System.Collections.Generic;
using Api.Permissions;
using Api.Contexts;
using Api.Eventing;
using System;
using Api.Startup;

namespace Api.CustomContentTypes
{
	/// <summary>
	/// Handles customContentTypes.
	/// Instanced automatically. Use injection to use this service, or Startup.Services.Get.
	/// </summary>
	public partial class CustomContentTypeService : AutoService<CustomContentType>
    {
		private readonly CustomContentTypeFieldService _fieldService;

		/// <summary>
		/// Instanced automatically. Use injection to use this service, or Startup.Services.Get.
		/// </summary>
		public CustomContentTypeService(CustomContentTypeFieldService fieldService) : base(Events.CustomContentType)
        {
			_fieldService = fieldService;

			// Example admin page install:
			InstallAdminPages("Content Types", "fa:fa-edit", new string[] { "id", "name" });

			Events.Service.AfterStart.AddEventListener(async (Context ctx, object x) =>
			{
				// Get all types:
				var allTypes = await Where(DataOptions.IgnorePermissions).ListAll(ctx);
				var allTypeFields = await fieldService.Where(DataOptions.IgnorePermissions).ListAll(ctx);

				// Load them now:
				await LoadCustomTypes(allTypes, allTypeFields);

				return x;
			});

			Events.CustomContentType.AfterCreate.AddEventListener(async (Context ctx, CustomContentType type) => {

				if (type == null)
				{
					return null;
				}

				await LoadCustomType(ctx, type);

				return type;
			});

			Events.CustomContentType.AfterUpdate.AddEventListener(async (Context ctx, CustomContentType type) => {

				if (type == null)
				{
					return null;
				}

				await LoadCustomType(ctx, type);

				return type;
			});

			Events.CustomContentType.AfterDelete.AddEventListener(async (Context ctx, CustomContentType type) => {

				if (type == null)
				{
					return null;
				}

				await UnloadCustomType(ctx, type.Id);

				return type;
			});

			Events.CustomContentType.Received.AddEventListener(async (Context ctx, CustomContentType type, int action) =>
			{
				if (type == null)
				{
					return null;
				}

				if (action == 3)
				{
					// Deleted
					await UnloadCustomType(ctx, type.Id);
				}
				else if (action == 1 || action == 2)
				{
					// Created/ updated on a remote server.
					await LoadCustomType(ctx, type);
				}

				return type;
			});

			// NB: Don't add handlers for changes on fields, given they will update in bulk and then the content type itself will update.
			/*
			Events.CustomContentTypeField.AfterCreate.AddEventListener(async (Context ctx, CustomContentTypeField field) => {

				if (field == null)
				{
					return null;
				}

				await LoadCustomType(ctx, field);

				return field;
			});

			Events.CustomContentTypeField.AfterUpdate.AddEventListener(async (Context ctx, CustomContentTypeField field) => {

				if (field == null)
				{
					return null;
				}

				await LoadCustomType(ctx, field);

				return field;
			});

			Events.CustomContentTypeField.AfterDelete.AddEventListener(async (Context ctx, CustomContentTypeField field) => {

				if (field == null)
				{
					return null;
				}

				await LoadCustomType(ctx, field);

				return field;
			});
			 */

		}

		/// <summary>
		/// Gets the latest generated controller types.
		/// </summary>
		/// <returns></returns>
		public Dictionary<uint, ConstructedCustomContentType> GetGeneratedTypes()
		{
			return loadedTypes;
		}

		/*
		/// <summary>
		/// Loads or reloads the given custom type by one of its fields. If you're doing this for more than one, use LoadCustomTypes instead.
		/// </summary>
		private async Task LoadCustomType(Context context, CustomContentTypeField field)
		{
			var type = await Get(context, field.CustomContentTypeId, DataOptions.IgnorePermissions);

			// Load it:
			await LoadCustomType(context, type);
		}
		*/

		/// <summary>
		/// Unloads a custom type of the given ID.
		/// </summary>
		/// <param name="context"></param>
		/// <param name="typeId"></param>
		/// <returns></returns>
		public async Task UnloadCustomType(Context context, uint typeId)
		{
			// Remove if exists.
			if (loadedTypes.TryGetValue(typeId, out ConstructedCustomContentType previousCompiledType))
			{
				// Shutdown this existing service.
				// This triggers a Delete event internally which 3rd party modules can attach to.
				await Services.StateChange(false, previousCompiledType.Service);
			}
		}

		/// <summary>
		/// Loads or reloads the given custom type. If you're doing this for more than one, use LoadCustomTypes instead.
		/// </summary>
		/// <param name="context"></param>
		/// <param name="type"></param>
		public async Task LoadCustomType(Context context, CustomContentType type)
		{
 			if (type == null)
			{
				return;
			}

			// Apply the fields:
			type.Fields = await _fieldService.Where("CustomContentTypeId=?").Bind(type.Id).ListAll(context);
			
			// Generate it now:
			var compiledType = TypeEngine.Generate(type);

			// Install it:
			var installMethod = GetType().GetMethod("InstallType");

			var setupType = installMethod.MakeGenericMethod(new Type[] {
				compiledType.ContentType
			});

			setupType.Invoke(this, new object[] { compiledType });
			
			// Signal a change to MVC:
			ActionDescriptorChangeProvider.Instance.HasChanged = true;
			ActionDescriptorChangeProvider.Instance.TokenSource.Cancel();
		}

		/// <summary>
		/// Loads the set of all types and fields now. This sets up their services and so on.
		/// </summary>
		/// <param name="types"></param>
		/// <param name="fields"></param>
		public async ValueTask LoadCustomTypes(List<CustomContentType> types, List<CustomContentTypeField> fields)
		{
			// Initial content type map:
			var map = new Dictionary<uint, CustomContentType>();

			foreach (var type in types)
			{
				map[type.Id] = type;
				type.Fields = null;
			}

			foreach (var field in fields)
			{
				if (field == null)
				{
					continue;
				}

				if (!map.TryGetValue(field.CustomContentTypeId, out CustomContentType contentType))
				{
					// Old field
					continue;
				}

				if (contentType.Fields == null)
				{
					contentType.Fields = new List<CustomContentTypeField>();
				}

				contentType.Fields.Add(field);
			}

			// Build the types:
			var compiledTypes = TypeEngine.Generate(types);

			var installMethod = GetType().GetMethod("InstallType");
			
			foreach (var compiledType in compiledTypes)
			{
				// Invoke InstallType:
				var setupType = installMethod.MakeGenericMethod(new Type[] {
					compiledType.ContentType
				});

				await (ValueTask)setupType.Invoke(this, new object[] { compiledType });
			}

			// New controller type - signal it:
			ActionDescriptorChangeProvider.Instance.HasChanged = true;
			ActionDescriptorChangeProvider.Instance.TokenSource.Cancel();
		}

		/// <summary>
		/// Raw controller types for custom types, mapped by CustomContentType.Id -> the constructed result.
		/// </summary>
		private Dictionary<uint, ConstructedCustomContentType> loadedTypes;

		/// <summary>
		/// Creates a service etc for the given system type and activates it. Invoked via reflection with a runtime compiled type.
		/// </summary>
		public async ValueTask InstallType<T>(ConstructedCustomContentType constructedType) where T : Content<uint>, new()
		{
			// Create event group for this custom svc:
			var events = new EventGroup<T>();

			// Create the service:
			constructedType.Service = new AutoService<T, uint>(events);

			if (loadedTypes == null)
			{
				loadedTypes = new Dictionary<uint, ConstructedCustomContentType>();
			}
			else
			{
				// Does it already exist? If so, we need to remove the existing loaded one.
				await UnloadCustomType(new Context(), constructedType.Id);
			}

			// Add it:
			loadedTypes[constructedType.Id] = constructedType;

			// Register:
			await Services.StateChange(true, constructedType.Service);
		}

	}
    
}
