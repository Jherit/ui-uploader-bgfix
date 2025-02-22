﻿using System;
using System.Threading.Tasks;
using Api.Database;
using Api.Emails;
using Microsoft.AspNetCore.Http;
using Api.Contexts;
using System.Collections.Generic;
using Api.Permissions;
using Api.Eventing;
using System.Collections;
using System.Reflection;
using Api.Startup;
using System.Linq;
using Api.Users;
using Api.PasswordResetRequests;

namespace Api.Invites
{

	/// <summary>
	/// Manages user roles.
	/// Instanced automatically. Use injection to use this service, or Startup.Services.Get.
	/// </summary>
	public partial class InviteService : AutoService<Invite>
    {
		/// <summary>
		/// Custom function to select the user to use from an email address.
		/// </summary>
		public Func<Context, string, ValueTask<User>> OnSelectUser;

		/// <summary>
		/// Custom function to determine if a user is registered
		/// </summary>
		public Func<User, bool> IsRegistered;

		/// <summary>
		/// The user service.
		/// </summary>
		public UserService _users;

		/// <summary>
		/// The email service.
		/// </summary>
		public EmailTemplateService _emails;

		/// <summary>
		/// Instanced automatically. Use injection to use this service, or Startup.Services.Get.
		/// </summary>
		public InviteService(UserService users, EmailTemplateService emails) : base(Events.Invite)
		{
			_users = users;
			_emails = emails;

			var config = GetConfig<InviteServiceConfig>();
			
			Events.Invite.BeforeCreate.AddEventListener(async (Context context, Invite invite) => {
				
				if(invite == null)
				{
					return invite;
				}
				
				// Generate a token, which will be hidden from the user except in the email/ whatever sends the invite:
				invite.Token = RandomToken.Generate(20);

				if (!string.IsNullOrEmpty(invite.EmailAddress))
				{
					// First, get the user. They may already exist.
					User user = null;

					if (OnSelectUser != null)
					{
						user = await OnSelectUser(context, invite.EmailAddress);
					}
					else
					{
						user = await users.Get(context, invite.EmailAddress);
					}
					
					if (!config.CanSendIfAlreadyExists && user != null)
					{
						// Throw an exception if the user has completed registration
						if ((IsRegistered != null && IsRegistered(user)) || (IsRegistered == null && user.PasswordHash != null))
                        {
							throw new PublicException("A user with that email address has an account already.", "user_exists");
						}
					}

					var fullName = (invite.FirstName != null && invite.LastName != null) ? invite.FirstName + " " + invite.LastName : null;

					if(user == null)
					{
						// Create the user now, as a member:
						user = await users.Create(context,
							new User() {
								Email = invite.EmailAddress,
								FirstName = invite.FirstName,
								LastName = invite.LastName,
								FullName = fullName
							}, DataOptions.IgnorePermissions);
					}
					
					// Set user ID:
					invite.InvitedUserId = user.Id;
				}

				return invite;
			});
			
			Events.Invite.AfterCreate.AddEventListener(async (Context context, Invite invite) => {
				
				// Send the email (we'll specifically wait for this one):
				if(invite == null || !invite.InvitedUserId.HasValue || invite.InvitedUserId == 0 || string.IsNullOrEmpty(invite.EmailAddress)){
					return invite;
				}

				var recipientUser = await _users.Get(context, invite.InvitedUserId.Value, DataOptions.IgnorePermissions);

				await SendInviteEmail(context, recipientUser, invite.Token);

				return invite;
				
			}, 100);
		}
		
		/// <summary>
		/// Redeem an invite by its token.
		/// </summary>
		public async ValueTask<Invite> Redeem(Context context, InviteData inviteData)
		{
			// Get the invite:
			var result = await Where("Token=?", DataOptions.IgnorePermissions).Bind(inviteData.Token).First(context);
			
			if(result == null)
			{
				throw new PublicException("This invite has expired", "invite_expired");
			}
			
			// Check expiry. They last 48 hours by default:
			if(DateTime.UtcNow > result.ExpiryUtc)
			{
				await Delete(context, result.Id, DataOptions.IgnorePermissions);
				throw new PublicException("This invite has expired", "invite_expired");
			}

			// Valid invite. If it's a user invite, it is set into the context:
			if (result.InvitedUserId.HasValue && result.InvitedUserId != 0)
			{
				var user = await _users.Get(context, result.InvitedUserId.Value, DataOptions.IgnorePermissions);

				if (user == null)
				{
					// The user account was deleted, which translates to an expired invite:
					throw new PublicException("This invite has expired", "invite_expired");
				}

				var loginResult = new LoginResult();
				loginResult.CookieName = Context.CookieName;
				loginResult.User = user;
				context.User = user;
				loginResult.LoginData = inviteData;

				// Act like a login:
				await Events.UserOnLogin.Dispatch(context, loginResult);

			}

			return result;
		}

		private async ValueTask SendInviteEmail(Context context, User recipientUser, string token)
		{
			var recipient = new Recipient(recipientUser);

			recipient.CustomData = new InviteCustomEmailData()
			{
				Token = token
			};

			var recipients = new List<Recipient>();
			recipients.Add(recipient);

			await _emails.SendAsync(
				recipients,
				"invited_join"
			);
		}

	}

	/// <summary>
	/// Custom data in invite emails
	/// </summary>
	public class InviteCustomEmailData
	{

		/// <summary>
		/// Token to use
		/// </summary>
		public string Token;

		/// <summary>
		/// The complete invite.
		/// </summary>
		public Invite Invite;
	}
}
