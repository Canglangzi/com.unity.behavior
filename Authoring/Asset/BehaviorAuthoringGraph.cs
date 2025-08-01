using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Behavior.GraphFramework;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Callbacks;
#endif

namespace Unity.Behavior
{
    /// <summary>
    /// The primary asset type used by Unity Behavior. The asset contains the authoring representation of the behavior
    /// graph.
    /// </summary>
    [Serializable]
#if UNITY_EDITOR
    [CreateAssetMenu(fileName = "Behavior Graph", menuName = "Behavior/Behavior Graph")]
#endif
    internal class BehaviorAuthoringGraph : GraphAsset, ISerializationCallbackReceiver
    {
#if UNITY_EDITOR
        [SerializeReference]
        private BehaviorGraphDebugInfo m_DebugInfo;
        public BehaviorGraphDebugInfo DebugInfo
        {
            get
            {
                if (m_DebugInfo == null)
                {
                    m_DebugInfo = GetOrCreateGraphDebugInfo(this);
                }
                return m_DebugInfo;
            }
        }

        [SerializeReference] private BehaviorGraph m_RuntimeGraph;
        public bool HasRuntimeGraph => m_RuntimeGraph != null && m_RuntimeGraph.RootGraph != null;
#endif

        [SerializeField]
        public SerializableGUID AssetID = SerializableGUID.Generate();

        [SerializeField]
        public StoryInfo Story = new StoryInfo();

        [NonSerialized]
        private List<NodeModel> m_RootNodes = new List<NodeModel>();
        public List<NodeModel> Roots
        {
            get
            {
                m_RootNodes.Clear();
                for (var i = 0; i < Nodes.Count; i++)
                {
                    if (Nodes[i] is BehaviorGraphNodeModel { IsRoot: true })
                    {
                        m_RootNodes.Add(Nodes[i]);
                    }
                }

                return m_RootNodes;
            }
        }

        [SerializeField]
        private List<NodeModelInfo> m_NodeModelsInfo;
        public IReadOnlyList<NodeModelInfo> NodeModelsInfo => m_NodeModelsInfo;

        private Dictionary<SerializableGUID, NodeModelInfo> m_RuntimeNodeTypeIDToNodeModelInfo;
        public IReadOnlyDictionary<SerializableGUID, NodeModelInfo> RuntimeNodeTypeIDToNodeModelInfo => m_RuntimeNodeTypeIDToNodeModelInfo;

        [SerializeField]
        internal List<BehaviorBlackboardAuthoringAsset> m_Blackboards = new List<BehaviorBlackboardAuthoringAsset>();

        [SerializeReference] private BehaviorBlackboardAuthoringAsset m_MainBlackboardAuthoringAsset;

        [Serializable]
        public struct NodeModelInfo
        {
            public string Name;
            public string Story;
            public SerializableGUID RuntimeTypeID;
            public List<VariableInfo> Variables;
            public List<string> NamedChildren;
        }

        [SerializeField]
        private SerializableCommandBuffer m_CommandBuffer = new SerializableCommandBuffer();
        public SerializableCommandBuffer CommandBuffer => m_CommandBuffer;

        [System.Serializable]
        public struct SubgraphGraphInfo
        {
            public SerializableGUID AssetID;
            public long Timestamp;
        }

        [SerializeField]
        private List<SubgraphGraphInfo> m_SubgraphsInfo = new();

        private long m_LastSerializedTimestamp;

        private void Awake()
        {
#if UNITY_EDITOR
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(GetInstanceID()));
            AssetID = new SerializableGUID(guid);
            EnsureAuthoringRuntimeGraphIsUpToDate();
#endif
            BehaviorGraphAssetRegistry.Add(this);
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            ValidateAssetNames(validateMainAsset: true);
        }

        /// <inheritdoc cref="OnValidate" />
        public override void OnValidate()
        {
#if UNITY_EDITOR
            EnsureAuthoringRuntimeGraphIsUpToDate();
#endif
            EnsureAtLeastOneRoot();
            EnsureStoryVariablesExist();
            EnsureAssetReferenceOnConditions();
            EnsureBlackboardsAreUpToDate();
            EnsureSubgraphsDependencyAreUpToDate();

            base.OnValidate();
        }

        public void AddOrUpdateDependency(BehaviorAuthoringGraph graph)
        {
            int index = m_SubgraphsInfo.FindIndex((info) => info.AssetID == graph.AssetID);
            var newData = new SubgraphGraphInfo()
            {
                AssetID = graph.AssetID,
                Timestamp = graph.m_VersionTimestamp,
            };

            if (index == -1)
            {
                m_SubgraphsInfo.Add(newData);
            }
            else if (m_SubgraphsInfo[index].Timestamp != newData.Timestamp)
            {
                m_SubgraphsInfo[index] = newData;
            }
            else
            {
                return;
            }

            SetAssetDirty(false);
        }

        public void RemoveDependency(BehaviorAuthoringGraph graph)
        {
            int index = m_SubgraphsInfo.FindIndex((info) => info.AssetID == graph.AssetID);
            if (index != -1)
            {
                m_SubgraphsInfo.RemoveAt(index);
                SetAssetDirty(true);
            }
        }

        public bool IsDependencyUpToDate(BehaviorAuthoringGraph graph)
        {
            int index = m_SubgraphsInfo.FindIndex((info) => info.AssetID == graph.AssetID);
            if (index == -1 || HasOutstandingChanges) // if the graph don't have dependency, or if it has pending outstanding changes.
            {
                return false;
            }

            return m_SubgraphsInfo[index].Timestamp == graph.m_VersionTimestamp;
        }

        public bool HasSubgraphDependency(BehaviorAuthoringGraph graph)
        {
            return m_SubgraphsInfo.Any((info) => info.AssetID == graph.AssetID);
        }

        private void EnsureSubgraphsDependencyAreUpToDate()
        {
            if (BehaviorGraphAssetRegistry.IsRegistryStateValid == false)
            {
                // If the registry is still collecting the asset, we might get false positive case of subgraph dependency lose.
                return;
            }

            bool hasChanged = false;
            for (int i = m_SubgraphsInfo.Count - 1; i >= 0; i--)
            {
                SubgraphGraphInfo subgraphInfo = m_SubgraphsInfo[i];
                if (!BehaviorGraphAssetRegistry.TryGetAssetFromId(subgraphInfo.AssetID, out var asset)
                    || !this.ContainsStaticSubgraphReferenceTo(asset))
                {
#if BEHAVIOR_DEBUG_ASSET_IMPORT
                    Debug.Log("Removing an invalid subgraph dependency", this);
#endif
                    // If the asset was deleted or this graph no longer contains reference the subgraph, then it is no longer dependent.
                    m_SubgraphsInfo.RemoveAt(i);
                    hasChanged = true;
                    continue;
                }

                // If the asset dependency is no longer up to date, we also need to rebuild the graph.
                hasChanged |= !IsDependencyUpToDate(asset);
            }

            if (hasChanged)
            {
                // Lost of static subgraph dependency means the graph module needs to be rebuilt.
                SetAssetDirty(setHasOutStandingChange: true);
            }
        }

        private void ValidateAssetNames(bool validateMainAsset)
        {
#if UNITY_EDITOR
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(this);
            string assetPathName = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            if (name != assetPathName)
            {
                name = assetPathName;
                if (!string.IsNullOrEmpty(assetPath))
                {
                    AssetDatabase.SetMainObject(this, assetPath);
                }
            }

            if (m_DebugInfo != null && m_DebugInfo.name != $"{name} Debug Info")
            {
                m_DebugInfo.name = $"{name} Debug Info";
            }
            if (m_RuntimeGraph != null && m_RuntimeGraph.name != name)
            {
                m_RuntimeGraph.name = name;
            }

            if (m_MainBlackboardAuthoringAsset != null && m_MainBlackboardAuthoringAsset.name != $"{name} Blackboard")
            {
                m_MainBlackboardAuthoringAsset.name = $"{name} Blackboard";
                if (m_MainBlackboardAuthoringAsset.RuntimeBlackboardAsset != null)
                {
                    m_MainBlackboardAuthoringAsset.RuntimeBlackboardAsset.name = $"{name} Blackboard";
                }
            }
#endif
        }

        private void EnsureBlackboardsAreUpToDate()
        {
            for (int i = m_Blackboards.Count - 1; i >= 0; i--)
            {
                BehaviorBlackboardAuthoringAsset blackboard = m_Blackboards[i];
                if (blackboard == null)
                {
                    m_Blackboards.RemoveAt(i);
                }
                else
                {
                    ValidateBlackboardAssetName(blackboard);
                    blackboard.OnValidate();
                }
            }
        }

        private void ValidateBlackboardAssetName(BehaviorBlackboardAuthoringAsset blackboard)
        {
#if UNITY_EDITOR
            if (AssetDatabase.GetAssetPath(blackboard) != AssetDatabase.GetAssetPath(this))
            {
                return;
            }

            if (blackboard.name != $"{name} + Blackboard")
            {
                blackboard.name = $"{name} + Blackboard";
            }
#endif
        }

        private void EnsureAtLeastOneRoot()
        {
            // Add a start node if no root exists.
            if (Roots.Count == 0)
            {
                var newStart = new StartNodeModel(NodeRegistry.GetInfo(typeof(Start)));
                newStart.OnDefineNode();
                Nodes.Add(newStart);
            }
        }

        internal void EnsureCorrectModelTypes()
        {
            for (int i = Nodes.Count - 1; i >= 0; i--)
            {
                NodeModel nodeModel = Nodes[i];
                if (nodeModel is not BehaviorGraphNodeModel node || nodeModel is PlaceholderNodeModel)
                {
                    continue;
                }
                NodeInfo info = NodeRegistry.GetInfoFromTypeID(node.NodeTypeID);
                if (info == null)
                {
                    // The node info is missing and it will be replaced with a placeholder node UI when opening the graph.
                    // We're keeping the serialization the same to avoid issues in case the node is recovered.
                }
                else if (node.NodeType.text != info.Type.AssemblyQualifiedName)
                {
                    node.NodeType = info.Type;
                }
            }
        }

        private void ReplaceNodeWithPlaceholder(BehaviorGraphNodeModel nodeModel)
        {
            if (RuntimeNodeTypeIDToNodeModelInfo.TryGetValue(nodeModel.NodeTypeID, out NodeModelInfo modelInfo) == false)
            {
                modelInfo = new NodeModelInfo();
                modelInfo.Name = "Missing Node";
                modelInfo.Story = "Missing Node";
            }
            modelInfo.RuntimeTypeID = nodeModel.NodeTypeID;
            PlaceholderNodeModel placeholderNodeModel = new PlaceholderNodeModel(modelInfo);
            if (typeof(CompositeNodeModel).IsAssignableFrom(nodeModel.GetType()))
            {
                placeholderNodeModel.PlaceholderType = PlaceholderNodeModel.PlaceholderNodeType.Composite;
            }
            else if (typeof(ModifierNodeModel).IsAssignableFrom(nodeModel.GetType()))
            {
                placeholderNodeModel.PlaceholderType = PlaceholderNodeModel.PlaceholderNodeType.Modifier;
            }
            else
            {
                placeholderNodeModel.PlaceholderType = PlaceholderNodeModel.PlaceholderNodeType.Action;
            }
            placeholderNodeModel.m_FieldValues = nodeModel.m_FieldValues;
            ReplaceNode(nodeModel, placeholderNodeModel);
        }

        private void ReplaceNode(BehaviorGraphNodeModel oldNodeModel, BehaviorGraphNodeModel newNodeModel)
        {
            newNodeModel.ID = oldNodeModel.ID;
            newNodeModel.Position = oldNodeModel.Position;
            newNodeModel.Asset = oldNodeModel.Asset;

            int nodeIndex = Nodes.FindIndex(node => node == oldNodeModel);
            if (nodeIndex != -1)
            {
                Nodes[nodeIndex] = newNodeModel;
            }

            foreach (NodeModel parent in oldNodeModel.Parents)
            {
                if (parent is SequenceNodeModel sequence)
                {
                    int childIndex = sequence.Nodes.FindIndex(node => node == oldNodeModel);
                    if (childIndex != -1)
                    {
                        sequence.Nodes[childIndex] = newNodeModel;
                    }
                }
                newNodeModel.Parents.Add(parent);
            }

            newNodeModel.PortModels.Clear();
            foreach (PortModel portModel in oldNodeModel.AllPortModels)
            {
                portModel.NodeModel = newNodeModel;
                newNodeModel.PortModels.Add(portModel);

                if (portModel.IsFloating)
                {
                    foreach (var connection in portModel.Connections)
                    {
                        if (connection.NodeModel is FloatingPortNodeModel floatingPortNodeModel)
                        {
                            // Added years ago. Need to investigate if there is something to finish here.
                            // Otherwise it is deadcode that needs to be removed.
                        }
                    }
                }
            }
        }

        private void EnsureStoryVariablesExist()
        {
            if (Blackboard == null)
            {
                return;
            }

            Story ??= new StoryInfo();

            List<VariableInfo> storyVariables = Story.Variables;
            List<VariableModel> assetVariables = Blackboard.Variables;
            foreach (VariableInfo storyVariable in storyVariables)
            {
                if (!assetVariables.Any(assetVar =>
                        string.Equals(assetVar.Name, storyVariable.Name, StringComparison.CurrentCultureIgnoreCase)
                        && assetVar.Type == (Type)storyVariable.Type))
                {
                    string varName = char.ToUpper(storyVariable.Name.First()) + storyVariable.Name.Substring(1);
                    Type varType = BlackboardUtils.GetVariableModelTypeForType(storyVariable.Type);
                    VariableModel variable = Activator.CreateInstance(varType) as VariableModel;
                    variable.Name = varName;
                    Blackboard.Variables.Add(variable);
                }
            }
        }

        private void EnsureAssetReferenceOnConditions()
        {
            foreach (NodeModel node in Nodes)
            {
                if (node is not IConditionalNodeModel conditionalNode)
                {
                    continue;
                }
                foreach (ConditionModel condition in conditionalNode.ConditionModels)
                {
                    condition.Asset = this;
                }
            }
        }

#if UNITY_EDITOR
        [OnOpenAsset(1)]
        public static bool step1(int instanceID, int line)
        {
            BehaviorAuthoringGraph asset = EditorUtility.InstanceIDToObject(instanceID) as BehaviorAuthoringGraph;
            if (asset == null)
            {
                BehaviorGraph runtimeGraph = EditorUtility.InstanceIDToObject(instanceID) as BehaviorGraph;
                if (runtimeGraph == null)
                {
                    return false;
                }
                if (!AssetDatabase.IsMainAsset(runtimeGraph) && AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GetAssetPath(runtimeGraph)) is BehaviorAuthoringGraph parentAsset)
                {
                    asset = parentAsset;
                }
                else
                {
                    return false;
                }
            }
            BehaviorWindowDelegate.Open(asset);
            return true; // we did not handle the open
        }
#endif

        public static BehaviorGraph GetOrCreateGraph(BehaviorAuthoringGraph assetObject)
        {
#if !UNITY_EDITOR
            return CreateInstance<BehaviorGraph>();
#else
            string assetPath = AssetDatabase.GetAssetPath(assetObject);
            if (!EditorUtility.IsPersistent(assetObject) || string.IsNullOrEmpty(assetPath))
            {
                return null;
            }

            BehaviorGraph graph = AssetDatabase.LoadAllAssetsAtPath(assetPath)
                .FirstOrDefault(asset => asset is BehaviorGraph) as BehaviorGraph;
            if (graph != null)
            {
                if (graph != assetObject.m_RuntimeGraph)
                {
                    assetObject.m_RuntimeGraph = graph;
                    assetObject.OnValidate();
                    AssetDatabase.SaveAssetIfDirty(assetObject);
                }

                return graph;
            }

            graph = CreateInstance<BehaviorGraph>();
            graph.name = assetObject.name;
            assetObject.m_RuntimeGraph = graph;
            AssetDatabase.AddObjectToAsset(graph, assetObject);
            assetObject.OnValidate(); // Can probably be remove in the future. 
            AssetDatabase.SaveAssetIfDirty(assetObject);
            return graph;
#endif
        }

#if UNITY_EDITOR
        public BehaviorGraphDebugInfo GetOrCreateGraphDebugInfo(BehaviorAuthoringGraph assetObject)
        {
            string assetPath = AssetDatabase.GetAssetPath(assetObject);
            if (!EditorUtility.IsPersistent(assetObject) || string.IsNullOrEmpty(assetPath))
            {
                return null;
            }

            BehaviorGraph graph = GetOrCreateGraph(assetObject);
            string graphPath = AssetDatabase.GetAssetPath(graph);
            BehaviorGraphDebugInfo debugInfo = AssetDatabase.LoadAssetAtPath<BehaviorGraphDebugInfo>(graphPath);
            if (!debugInfo)
            {
                debugInfo = CreateInstance<BehaviorGraphDebugInfo>();
                debugInfo.name = assetObject.name + " Debug Info";
                AssetDatabase.AddObjectToAsset(debugInfo, graph);
                EditorUtility.SetDirty(assetObject);
                AssetDatabase.SaveAssetIfDirty(assetObject);
            }
            assetObject.m_DebugInfo = debugInfo;
            return debugInfo;
        }
#endif

        /// <inheritdoc cref="OnBeforeSerialize"/>
        public void OnBeforeSerialize()
        {
            if (VersionTimestamp == m_LastSerializedTimestamp)
            {
                return;
            }

            CreateNodeModelsInfoCache();
            m_LastSerializedTimestamp = VersionTimestamp;
        }

        /// <inheritdoc cref="OnAfterDeserialize"/>
        public void OnAfterDeserialize()
        {
            CreateNodeModelInfosDictionaryFromList();
        }

        private void CreateNodeModelsInfoCache()
        {
            var oldNodeModelsInfos = m_NodeModelsInfo;
            m_RuntimeNodeTypeIDToNodeModelInfo ??= new Dictionary<SerializableGUID, NodeModelInfo>();
            m_NodeModelsInfo = new List<NodeModelInfo>();
            m_RuntimeNodeTypeIDToNodeModelInfo.Clear();
            foreach (NodeModel node in Nodes)
            {
                if (node is not BehaviorGraphNodeModel behaviorNode)
                {
                    continue;
                }
                NodeInfo nodeInfo = NodeRegistry.GetInfoFromTypeID(behaviorNode.NodeTypeID);
                if (nodeInfo == null || m_RuntimeNodeTypeIDToNodeModelInfo.ContainsKey(nodeInfo.TypeID))
                {
                    continue;
                }
                NodeModelInfo nodeModelInfo = new NodeModelInfo
                {
                    Name = nodeInfo.Name,
                    Story = nodeInfo.Story,
                    RuntimeTypeID = nodeInfo.TypeID,
                    Variables = nodeInfo.Variables.Select(variable => new VariableInfo
                    {
                        Name = variable.Name,
                        Type = (typeof(BlackboardVariable).IsAssignableFrom(variable.Type.Type) && variable.Type.Type.IsGenericType) ? variable.Type.Type.GenericTypeArguments[0] : variable.Type
                    }).ToList(),
                    NamedChildren = nodeInfo.NamedChildren
                };
                m_RuntimeNodeTypeIDToNodeModelInfo[nodeInfo.TypeID] = nodeModelInfo;
                m_NodeModelsInfo.Add(nodeModelInfo);
            }

            // Add previously saved node model infos if they're missing in the current collection.
            if (oldNodeModelsInfos != null)
            {
                foreach (NodeModelInfo nodeInfo in oldNodeModelsInfos)
                {
                    if (m_RuntimeNodeTypeIDToNodeModelInfo.ContainsKey(nodeInfo.RuntimeTypeID))
                    {
                        continue;
                    }
                    m_RuntimeNodeTypeIDToNodeModelInfo.Add(nodeInfo.RuntimeTypeID, nodeInfo);
                    m_NodeModelsInfo.Add(nodeInfo);
                }
            }
        }

        private void CreateNodeModelInfosDictionaryFromList()
        {
            if (m_RuntimeNodeTypeIDToNodeModelInfo == null)
            {
                m_RuntimeNodeTypeIDToNodeModelInfo = new Dictionary<SerializableGUID, NodeModelInfo>();
            }
            if (m_NodeModelsInfo == null)
            {
                return;
            }
            foreach (NodeModelInfo nodeModelInfo in m_NodeModelsInfo)
            {
                m_RuntimeNodeTypeIDToNodeModelInfo[nodeModelInfo.RuntimeTypeID] = nodeModelInfo;
            }
        }

        internal override void EnsureAssetHasBlackboard()
        {
            string blackboardName = name + " Blackboard";
#if UNITY_EDITOR
            string path = AssetDatabase.GetAssetPath(this);
            BlackboardAsset existingBlackboard = AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault(asset => asset is BlackboardAsset) as BlackboardAsset;
            BehaviorBlackboardAuthoringAsset blackboardAuthoring = existingBlackboard as BehaviorBlackboardAuthoringAsset;
            if (blackboardAuthoring == null)
            {
                blackboardAuthoring = CreateInstance<BehaviorBlackboardAuthoringAsset>();

                Blackboard = blackboardAuthoring;
                Blackboard.name = blackboardName;
                Blackboard.hideFlags = HideFlags.HideInHierarchy;
                m_MainBlackboardAuthoringAsset = blackboardAuthoring;

                if (existingBlackboard != null)
                {
                    Blackboard.AssetID = existingBlackboard.AssetID;
                    Blackboard.Variables = existingBlackboard.Variables;
                    AssetDatabase.RemoveObjectFromAsset(existingBlackboard);
                }
            }
            else if (blackboardAuthoring != null)
            {
                m_MainBlackboardAuthoringAsset = blackboardAuthoring;
                if (Blackboard != null && blackboardAuthoring == Blackboard)
                {
                    // Update the graph Blackboard name if needed.
                    if (blackboardAuthoring.name != blackboardName)
                    {
                        blackboardAuthoring.name = blackboardName;
                        if (blackboardAuthoring.RuntimeBlackboardAsset != null)
                        {
                            blackboardAuthoring.RuntimeBlackboardAsset.name = blackboardName;
                        }
                    }
                    Blackboard.hideFlags = HideFlags.HideInHierarchy;
                }
                return;
            }

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            // If we reached this far, that means we are generating a new blackboard.
            AssetDatabase.AddObjectToAsset(Blackboard, this);
            blackboardAuthoring.BuildRuntimeBlackboard();
            AssetDatabase.SaveAssetIfDirty(this);
#endif
        }

        public override string ToString() => name;

        public void OnDestroy()
        {
            BehaviorGraphAssetRegistry.Remove(this);
        }

        /// <summary>
        /// Build the sub assets (Runtime Graph, Runtime Blackboard Asset, Debug Info).
        /// </summary>
        /// <param name="forceRebuild">Set to false allows to skip the full rebuild and just update VersionTimestamp in case there is no outstanding change.
        /// Default to true to preserve original behavior.</param>
        /// <returns>The up-to-date BehaviorGraph.</returns>
        public BehaviorGraph BuildRuntimeGraph(bool forceRebuild = true)
        {
            var runtimeGraph = GetOrCreateGraph(this);
            if (runtimeGraph == null)
            {
                return null;
            }

            // Some situations might require a full rebuild (new root, outdated subgraph dependency)
            OnValidate();

            if (runtimeGraph.RootGraph == null || HasOutstandingChanges || forceRebuild)
            {
#if BEHAVIOR_DEBUG_ASSET_IMPORT
                Debug.Log($"GraphAsset[<b>{name}</b>].BuildRuntimeGraph", this);
#endif
                runtimeGraph.Graphs.Clear();
                var graphAssetProcessor = new GraphAssetProcessor(this, runtimeGraph);
                graphAssetProcessor.ProcessGraph();
                SetDirtyAndSyncRuntimeGraphTimestamp(outstandingChange: true);
            }
#if UNITY_EDITOR
            else if (EditorUtility.IsDirty(this))
            {
#if BEHAVIOR_DEBUG_ASSET_IMPORT
                Debug.Log($"GraphAsset[<b>{name}</b>].SetAssetDirty - Asset don't have outstanding change but serialized data are dirty.", this);
#endif
                // If no outstanding change but serialized data are dirty, only update the timestamp.
                // This happens when only serialized field of cosmetic data are changed (like node position).
                SetDirtyAndSyncRuntimeGraphTimestamp(outstandingChange: false);
            }
#endif

            return runtimeGraph;
        }

        private void SetDirtyAndSyncRuntimeGraphTimestamp(bool outstandingChange)
        {
            SetAssetDirty(outstandingChange); // Set dirty update the timestamp of the main asset.
#if UNITY_EDITOR // We really need to make Authoring assembly editor only to be able to break free it.
            m_RuntimeGraph.RootGraph.VersionTimestamp = VersionTimestamp; // So we need to sync it again here.
#endif
        }

        public override void SaveAsset()
        {
            var authoringBB = Blackboard as BehaviorBlackboardAuthoringAsset;
            if (authoringBB != null)
            {
                bool isBlackboardDirty = !authoringBB.IsAssetVersionUpToDate();
                if (isBlackboardDirty)
                {
                    authoringBB.BuildRuntimeBlackboard();
                }
                
                // Always rebuild runtime data if needed.
                BuildRuntimeGraph(forceRebuild: isBlackboardDirty);
                authoringBB.SaveAsset();
            }

            base.SaveAsset();
        }

#if UNITY_EDITOR
        /// <summary>
        /// Custom version of SaveAsset made for BehaviorAssetPostProcessor.
        /// This method manually rebuilds without saving the asset. Also rebuild the embedded asset if needed.
        /// This is required in order to prevent the asset being rebuilt everytime until GraphAsset.SaveAsset is called.
        /// After the rebuild, reset HasOutstandingChanges, readying the asset to be manually saved using AssetDatabase.SaveAsset.
        /// </summary>
        /// <remark>
        /// HasOutstandingChanges is internal to the Behavior.GraphFramework assembly.
        /// </remark>
        internal void RebuildGraphAndBlackboardRuntimeData()
        {
            if (Blackboard == null)
            {
                EnsureAssetHasBlackboard();
            }

            if (Blackboard is BehaviorBlackboardAuthoringAsset authoringBB)
            {
                if (!authoringBB.IsAssetVersionUpToDate())
                {
                    authoringBB.BuildRuntimeBlackboard();
                }

                BuildRuntimeGraph();
                authoringBB.HasOutstandingChanges = false;
                HasOutstandingChanges = false;
            }
        }

        /// <summary>
        /// This method is used to validate the serialized runtime graph that has been generated during authoring time.
        /// This is needed to check to ensure backward compatibility with older graph that didn't serialized a
        /// direct link to the runtime asset.
        /// </summary>
        /// <param name="assetObject"></param>
        /// <returns></returns>
        public void EnsureAuthoringRuntimeGraphIsUpToDate()
        {
            if (!EditorUtility.IsPersistent(this))
            {
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(this);
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            BehaviorGraph graph = AssetDatabase.LoadAllAssetsAtPath(assetPath)
                .FirstOrDefault(asset => asset is BehaviorGraph) as BehaviorGraph;
            if (graph != null)
            {
                // Newly created graph soon to be generated.
                if (m_RuntimeGraph == null || m_RuntimeGraph.RootGraph == null)
                {
                    return;
                }

                // This usually happen when the asset is duplicated.
                if (graph != this.m_RuntimeGraph)
                {
                    m_RuntimeGraph = graph;
                }

                if (m_RuntimeGraph.RootGraph.AuthoringAssetID != AssetID)
                {
                    m_RuntimeGraph.RootGraph.AuthoringAssetID = AssetID;
                }

                if (m_RuntimeGraph.RootGraph.VersionTimestamp != VersionTimestamp)
                {
                    SetDirtyAndSyncRuntimeGraphTimestamp(outstandingChange: false);
                }
            }
            else if (m_RuntimeGraph != null)
            {
                // In this case, the asset lost reference to it's runtime graph (deleted).
                m_RuntimeGraph = null;
                SetAssetDirty(true);
            }
        }
#endif
    }
}