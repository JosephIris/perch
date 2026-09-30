// The pairings, one per computer, kept in the Keychain (the token is the
// secret). Scanning a second computer's code adds to the list; scanning the
// same computer again replaces its entry.

import Foundation
import Security

enum PairingStore {
    private static let service = "com.buildwithperch.remote"
    private static let account = "pairings"

    static func load() -> [Pairing] {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var out: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &out) == errSecSuccess,
              let data = out as? Data,
              let list = try? JSONDecoder().decode([Pairing].self, from: data)
        else { return [] }
        return list
    }

    static func save(_ pairings: [Pairing]) {
        let base: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        guard let data = try? JSONEncoder().encode(pairings) else { return }
        let attrs: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        let status = SecItemUpdate(base as CFDictionary, attrs as CFDictionary)
        if status == errSecItemNotFound {
            SecItemAdd(base.merging(attrs) { $1 } as CFDictionary, nil)
        }
    }
}
