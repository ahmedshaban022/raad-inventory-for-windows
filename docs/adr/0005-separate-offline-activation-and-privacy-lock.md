# Separate offline activation from the optional privacy lock

The user accepted one-time offline activation using a password and a separate optional privacy lock with its own password, disabled by default. When the privacy lock is enabled, the application will offer an explicit lock action and lock when the Windows session is locked.

This preserves the distinction between initial permission to use the application and a user's day-to-day privacy control, without an online activation service. The accepted privacy model protects access through the application interface on the operator's personal Windows account; v1 does not encrypt the database or backup files. Enabling the privacy lock presents a recovery code, while activation state and lock credentials remain installation-local and are excluded from business-data backups and Android transfers.

If both the password and recovery code are lost, provide a documented local support procedure for the owner of the Windows account that resets only the privacy lock while preserving business data and activation. Ordinary business-backup restore and Android import preserve the destination installation's existing lock. A portable dataset is not an activation or privacy-lock credential.
