// Copyright 2021 Google LLC
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

/* Post build processor for configuring the XCode project generated by Unity.
 *
 * Requires Unity 5.0 or higher.
 *
 * Given that Unity generates the XCode project, we cannot configure a project
 * in advance with the necessary frameworks and capabilities. Instead, we do so
 * programmatically via a post build processor. This will automatically run
 * once Unity is finished building for iOS.
 *
 * There are three main operations performed by this script:
 * (1) Copy over an existing entitlement to the XCode project.
 * (2) Patch the project's Info.plist for certain capabilities.
 * (3) Add frameworks to the project.
 *
 * For (1), the entitlement is generated manually in advance by
 * configuring a separate XCode project to use the desired capabilities
 * and frameworks, and saving the generated entitlement. That entitlement
 * is then copied into the Unity project before building for iOS, and is saved
 * as 'dev.entitlements'.
 *
 * For (2), we similarly see the difference between a project with the
 * capabilities/frameworks versus without them, and patch the Info.plist
 * using Unity's plist API. We could have saved a copy of an Info.plist
 * and copied that over, like we did in (1), but the Info.plist contains a
 * large amount of other critical information. Patching the plist appears
 * to involve less of a maintenance burden.
 *
 * For (3), Unity has an API for adding frameworks.
 *
 * Note that starting with Unity 2017, there is a much more powerful
 * API for enabling XCode capabilities which would significantly simplify
 * this script. If support for 5.x is deprecated, then moving to that API
 * is recommended. See the ProjectCapabilityManager in Unity's documentation
 * for reference.
 */

#if UNITY_IOS
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

public sealed class XcodeCapabilities
{
  [PostProcessBuild]
  public static void OnPostprocessBuild (BuildTarget buildTarget, string path)
  {
    if (buildTarget != BuildTarget.iOS) {
      return;
    }

    string projectPath = PBXProject.GetPBXProjectPath(path);
    var tempProject = new PBXProject();
    tempProject.ReadFromString(File.ReadAllText(projectPath));
    string targetId = GetMainTargetGUID(tempProject);

    // This will look for an entitlements file in the unity project, and add it to the
    // PBX Project if found.
    AddEntitlements(tempProject, path, targetId);

    if (path.Contains("FirebaseMessaging")) {
      MakeChangesForMessaging(tempProject, path, targetId);
    }
    if (path.Contains("FirebaseAuth")) {
      MakeChangesForAuth(tempProject, path, targetId);
    }
    // Bitcode is unnecessary, as we are not submitting these to the Apple store.
    tempProject.SetBuildProperty(targetId, "ENABLE_BITCODE", "NO");
    File.WriteAllText(projectPath, tempProject.WriteToString());
  }

  static void AddEntitlements(PBXProject project, string path, string targetId){
    string[] entitlements = AssetDatabase.FindAssets("dev")
      .Select(AssetDatabase.GUIDToAssetPath)
      .Where(p => p.Contains("dev.entitlements"))
      .ToArray();
    // Only some APIs require entitlements so length 0 is okay
    if (entitlements.Length == 0) {
      Debug.Log("No entitlement file found.");
      return;
    }
    if (entitlements.Length > 1) {
      throw new System.InvalidOperationException("Multiple entitlements found.");
    }
    Debug.Log("Entitlement file found.");
    string entitlementPath = entitlements[0];

    string entitlementFileName = Path.GetFileName(entitlementPath);
    string relativeDestination = Google.IOSResolver.PROJECT_NAME + "/" + entitlementFileName;
    FileUtil.CopyFileOrDirectory(entitlementPath, path + "/" + relativeDestination);
    project.AddFile(relativeDestination, entitlementFileName);
    // Not enough to add the entitlement: need to force the project to recognize it.
    project.AddBuildProperty(targetId, "CODE_SIGN_ENTITLEMENTS", relativeDestination);
    Debug.Log("Added entitlement to xcode project.");
  }

  static void MakeChangesForMessaging(PBXProject project, string path, string targetId) {
    Debug.Log("Messaging testapp detected.");
    AddFramework(project, targetId, "UserNotifications.framework");
    EnableRemoteNotification(project, path, targetId);
    Debug.Log("Finished making messaging-specific changes.");
  }

  static void MakeChangesForAuth(PBXProject project, string path, string targetId) {
    Debug.Log("Auth testapp detected.");
    AddFramework(project, targetId, "UserNotifications.framework");
    Debug.Log("Finished making auth-specific changes.");
  }

  static void EnableRemoteNotification(PBXProject project, string path, string targetId) {
    Debug.Log("Adding remote-notification to UIBackgroundModes");
    var plist = new PlistDocument();
    string plistPath = path + "/Info.plist";
    plist.ReadFromString(File.ReadAllText(plistPath));
    PlistElementDict rootDict = plist.root;
    PlistElementArray backgroundModes = rootDict.CreateArray("UIBackgroundModes");
    backgroundModes.AddString("remote-notification");
    File.WriteAllText(plistPath, plist.WriteToString());
    Debug.Log("Finished adding remote-notification.");
  }

  static void AddFramework(PBXProject project, string targetId, string framework) {
    Debug.LogFormat("Adding framework to xcode project: {0}.", framework);
    project.AddFrameworkToProject(targetId, framework, false);
    Debug.Log("Finished adding framework.");
  }

  static string GetMainTargetGUID(PBXProject pbxProject) {
    // In 2019.3 Unity changed this API without an automated update path via the api-updater.
    // There doesn't seem to be a clean version-independent way to handle this logic.
    #if UNITY_2019_3_OR_NEWER
      return pbxProject.GetUnityMainTargetGuid();
    #else
      return pbxProject.TargetGuidByName(PBXProject.GetUnityTargetName());
    #endif
  }
}
#endif