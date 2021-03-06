// This file is part of Tmds.Ssh which is released under MIT.
// See file LICENSE for full license details.

using System;

namespace Tmds.Ssh
{
    sealed public class PasswordCredential : Credential
    {
        public PasswordCredential(string password)
        {
            Password = password ?? throw new ArgumentNullException(nameof(password));
        }

        internal string Password { get; }
    }
}
