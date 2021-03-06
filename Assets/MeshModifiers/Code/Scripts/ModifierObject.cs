﻿using UnityEngine;
using UnityEngine.Events;
using System.Linq;
using System.Collections;
using System.Collections.Generic;

namespace MeshModifiers
{
	[RequireComponent (typeof (MeshFilter))]
	public class ModifierObject : MonoBehaviour
	{
		public bool autoUpdate = true;

		[Range (0f, 1f)]
		public float modifierStrength = 1f;
		public enum ModifierStrengthVertexMask { None, R, G, B, A }
		[Tooltip (MeshModifierConstants.MODIFIER_STRENGTH_VERTEX_MASK_TOOLTIP)]
		public ModifierStrengthVertexMask modifierStrengthVertexMask = ModifierStrengthVertexMask.None;

		public bool updateNormals = true;

		[System.NonSerialized, Tooltip (MeshModifierConstants.UPDATE_WHEN_HIDDEN_TOOLTIP)]
		public bool updateWhenHidden = false;

		[Range (1, MeshModifierConstants.MAX_MOD_FRAMES), Tooltip (MeshModifierConstants.MODIFY_FRAMES_TOOLTIP)]
		public int modifyFrames = 1;

		[System.NonSerialized]
		public List<MeshModifierBase> modifiers = new List<MeshModifierBase> ();

		[System.NonSerialized]
		public bool refreshModifiersEveryFrame = true;

		/// <summary>
		/// Invoked before modifiers are auto-applied.
		/// </summary>
		[System.NonSerialized]
		public UnityEvent OnAutoUpdateStart = new UnityEvent ();
		/// <summary>
		/// Invoked after modifiers are auto-applied.
		/// </summary>
		[System.NonSerialized]
		public UnityEvent OnAutoUpdateFinish = new UnityEvent ();

		/// <summary>
		/// Invoked before a chunk AutoApplyModifiers coroutine is modified.
		/// </summary>
		[System.NonSerialized]
		public UnityEvent OnAutoChunkStart = new UnityEvent ();
		/// <summary>
		/// Invoked after a chunk AutoApplyModifiers coroutine is modified.
		/// </summary>
		[System.NonSerialized]
		public UnityEvent OnAutoChunkFinish = new UnityEvent ();

		private Vector3[] baseVertices;
		private Vector3[] modifiedVertices;
		private Vector3[] baseNormals;
		private Vector3[] modifiedNormals;
		private Vector4[] baseTangents;
		private Color[] colors;

		/// <summary>
		/// Updated based on Unity's OnWillRenderObject and OnBecameVisible.
		/// </summary>
		public bool IsVisible { get; private set; }

		/// <summary>
		/// The index that corresponds the current chunk of vertices being modified.
		/// </summary>
		public int CurrentModifiedChunkIndex { get; private set; }

		/// <summary>
		/// Local to the modifier object. Used to sync modifications that occur over multiple frames that require a time value (like an animated sin-wave or noise).
		/// </summary>
		public float Time { get; private set; }

		private MeshFilter filter;
		public MeshFilter Filter
		{
			get { return filter ?? (filter = GetComponent<MeshFilter> ()); }
			set { filter = value; }
		}

		private Mesh mesh;
		public Mesh Mesh
		{
			get
			{
				if (mesh == null)
					mesh = filter.mesh;
				return mesh;
			}
			set { mesh = value; }
		}


		private void Awake ()
		{
			Filter = GetComponent<MeshFilter> ();
		}
		private void Start ()
		{
			ChangeMesh (Filter.sharedMesh);
			RefreshModifiers ();

			StartCoroutine (AutoApplyModifiers ());
#if UNITY_EDITOR
			UnityEditor.Undo.undoRedoPerformed += () =>
			{
				ModifyAll ();
			};
#endif
		}
		private void OnWillRenderObject ()
		{
			IsVisible = true;
		}
		private void OnBecameInvisible ()
		{
			IsVisible = false;
		}


		/// <summary>
		/// Modifies the whole mesh, as one chunk.
		/// [HINT]: This method only changes the internal data, call ApplyModifications for the mesh data to reflect the internal data.
		/// </summary>
		public void ModifyAll (bool invokePreMods = true, bool invokePostMods = false)
		{
			var useableModifiers = GetUseableModifiers ();

			if (invokePreMods)
				InvokePreMods (useableModifiers);

			ModifyChunk (0, baseVertices.Length, useableModifiers);

			if (invokePostMods)
				InvokePostMods (useableModifiers);
		}

		/// <summary>
		/// Modifies the whole mesh, as one chunk.
		/// [HINT]: This method only changes the internal data, call ApplyModifications for the mesh data to reflect the internal data.
		/// </summary>
		public void ModifyAll (int[] modifierIndexes, bool invokePreMods = true, bool invokePostMods = false)
		{
			if (invokePreMods)
				InvokePreMods (modifierIndexes);

			ModifyChunk (0, baseVertices.Length, modifierIndexes);

			if (invokePostMods)
				InvokePostMods (modifierIndexes);
		}

		/// <summary>
		/// Modifies chunk of the mesh. If modifiersIndexes is null, all useable modifiers will be applied.
		/// [HINT]: This method only changes the internal data, call ApplyModifications for the mesh data to reflect the internal data.
		/// [HINT]: Any modifiers that inherit the PreMod or PostMod method won't work without InvokePreMods and/or InvokePostMods being called. This allows you to call those methods once (before a set of chunks are processed), rather than everytime you change a chunk.
		/// </summary>
		public void ModifyChunk (int startIndex, int stopIndex, int[] modifierIndexes = null)
		{
			// If modifierIndexes are not supplied, use all useabled modifiers.
			if (modifierIndexes == null)
				modifierIndexes = GetUseableModifiers ();

			// For each vertex in this chunk...
			for (var currentVert = startIndex; currentVert < stopIndex; currentVert++)
			{
				// For each modifier...
				foreach (int index in modifierIndexes)
					modifiedVertices[currentVert] = modifiers[index].ModifyOffset (ConstructData (currentVert));

				PostModPointOperation (currentVert);
			}

			if (updateNormals)
				Mesh.RecalculateNormals ();
		}
		private VertexData ConstructData (int index)
		{
			return new VertexData (baseVertices[index], modifiedVertices[index], modifiedNormals[index], baseTangents[index]);
		}

		/// <summary>
		/// Call this after calling ModifyAll or ModifyChunk for the changes to be applied the the actual mesh.
		/// </summary>
		public void ApplyModifications ()
		{
			// Update the mesh's vertices to reflect the modified vertices.
			Mesh.SetVertices (modifiedVertices.ToList ());

			if (updateNormals)
				Mesh.RecalculateNormals ();
			Mesh.RecalculateBounds ();

			// Reset the modded vertices to their base state so next frames modifications are based on the original vertices.
			ResetVerticesAndNormals ();
		}

		/// <summary>
		/// A safe way to change the modified mesh.
		/// </summary>
		public void ChangeMesh (Mesh newMesh)
		{
			Filter.mesh = newMesh;
			Mesh = Filter.mesh;
			Mesh.MarkDynamic ();

			baseVertices = (Vector3[])Filter.sharedMesh.vertices.Clone ();
			baseNormals = (Vector3[])Filter.sharedMesh.normals.Clone ();
			baseTangents = (Vector4[])Filter.sharedMesh.tangents.Clone ();
			colors = (Color[])Filter.sharedMesh.colors.Clone ();
			ResetVerticesAndNormals ();
		}

		/// <summary>
		/// Updates the list of modifiers, including their order of execution.
		/// </summary>
		public void RefreshModifiers ()
		{
			if (modifiers == null)
				modifiers = new List<MeshModifierBase> ();
			try
			{
				modifiers = GetComponents<MeshModifierBase> ().ToList ();
			}
			catch
			{
				// I don't know why calling GetComponents says that ModifierObject is null!
			}
		}

		/// <summary>
		/// Reverts any changes made to the vertices and their normals, and updates the base vertices and normals in case custom code has changed the mesh.
		/// </summary>
		private void ResetVerticesAndNormals ()
		{
			modifiedVertices = (Vector3[])baseVertices.Clone ();
			modifiedNormals = (Vector3[])baseNormals.Clone ();
		}

		/// <summary>
		/// Returns the number of vertices and handles missing references
		/// </summary>
		public int GetVertCount ()
		{
			return (Filter && Filter.sharedMesh) ? Filter.sharedMesh.vertexCount : 0;
		}

		/// <summary>
		/// Returns the position of a vertex and handles missing references.
		/// </summary>
		public Vector3 GetVertPosition (int index)
		{
			return (Filter.sharedMesh) ? transform.rotation * Filter.sharedMesh.vertices[index] : Vector3.zero;
		}

		/// <summary>
		/// Returns the world position of a vertex at the given index and handles missing references.
		/// </summary>
		public Vector3 GetVertWorldPosition (int index)
		{
			return transform.position + GetVertPosition (index);
		}

		/// <summary>
		/// Gets the number of vertices being modified each frame.
		/// </summary>
		public int GetModifiedVertsPerFrame ()
		{
			return GetVertCount () / modifyFrames;
		}

		/// <summary>
		/// Gets the number of vertices being modified per second.
		/// </summary>
		public int GetModifiedVertsPerSecond ()
		{
			return (int)(60f * GetModifiedVertsPerFrame ());
		}

		/// <summary>
		/// Returns the current state of the modified vertices.
		/// </summary>
		public Vector3[] GetCurrentVerts ()
		{
			return modifiedVertices.Clone () as Vector3[];
		}

		/// <summary>
		/// Returns an array of the vertices' world positions.
		/// </summary>
		public Vector3[] GetCurrentWorldVerts ()
		{
			var worldVerts = GetCurrentVerts ();

			for (var i = 0; i < worldVerts.Length; i++)
				worldVerts[i] = (Mathx.Vectorx.Multiply (transform.rotation * worldVerts[i], transform.localScale)) + transform.position;

			return worldVerts;
		}

		/// <summary>
		/// Returns an array of modifiers that wants to be used.
		/// </summary>
		public int[] GetUseableModifiers ()
		{
			RefreshModifiers ();
			var modIndexes = new List<int> ();

			for (var i = 0; i < modifiers.Count; i++)
			{
				if (modifiers[i] == null)
				{
					modifiers.RemoveAt (i);
					i--;
					continue;
				}
				if (modifiers[i].update)
					modIndexes.Add (i);
			}

			return modIndexes.ToArray ();
		}

		/// <summary>
		/// Returns the bounds of the original mesh.
		/// </summary>
		public Bounds GetBounds ()
		{
			return new Bounds (Filter.mesh.bounds.center, Filter.mesh.bounds.size);
		}

		/// <summary>
		/// Should this object perform modifications to the mesh?
		/// </summary>
		private bool ShouldModify ()
		{
			return modifiers.Count > 0 && modifiers != null && (IsVisible || updateWhenHidden) && autoUpdate;
		}

		/// <summary>
		/// Invokes the PreMod methods on all of the modifiers.
		/// </summary>
		private void InvokePreMods (int[] modIndexes)
		{
			foreach (var index in modIndexes)
				modifiers[index].PreMod ();
		}

		/// <summary>
		/// Invokes the PostMod methods on all of the modifiers.
		/// </summary>
		private void InvokePostMods (int[] modIndexes)
		{
			foreach (var index in modIndexes)
				modifiers[index].PostMod ();
		}

		/// <summary>
		/// This is where any final modifications will be performed.
		/// </summary>
		private void PostModPointOperation (int currentVertex)
		{
			if (!Mathf.Approximately (modifierStrength, 1f))
				modifiedVertices[currentVertex] = Vector3.Lerp (baseVertices[currentVertex], modifiedVertices[currentVertex], modifierStrength);
			if (colors.Length != modifiedVertices.Length)
				return;
			switch (modifierStrengthVertexMask)
			{
				case ModifierStrengthVertexMask.None:
					break;
				case ModifierStrengthVertexMask.R:
					modifiedVertices[currentVertex] = Vector3.Lerp (baseVertices[currentVertex], modifiedVertices[currentVertex], colors[currentVertex].r);
					break;
				case ModifierStrengthVertexMask.G:
					modifiedVertices[currentVertex] = Vector3.Lerp (baseVertices[currentVertex], modifiedVertices[currentVertex], colors[currentVertex].g);
					break;
				case ModifierStrengthVertexMask.B:
					modifiedVertices[currentVertex] = Vector3.Lerp (baseVertices[currentVertex], modifiedVertices[currentVertex], colors[currentVertex].b);
					break;
				case ModifierStrengthVertexMask.A:
					modifiedVertices[currentVertex] = Vector3.Lerp (baseVertices[currentVertex], modifiedVertices[currentVertex], colors[currentVertex].a);
					break;
			}
		}


		/// <summary>
		/// Modifies multiple chunks over x frames to lessen performance impact.
		/// </summary>
		private IEnumerator AutoApplyModifiers ()
		{
			while (true)
			{
				if (ShouldModify ())
				{
					OnAutoUpdateStart.Invoke ();

					CurrentModifiedChunkIndex = 0;

					// Store the number of chunks in a local variable so that if modifyFrames is changed before the modifications are complete, nothing will get messed up.
					var chunkCount = modifyFrames;
					// Find the approximate number of vertices in a single split.
					var chunkSize = Mesh.vertexCount / chunkCount;

					// Increment time based on how much approximate time will have passed when the mods are done.
					Time += UnityEngine.Time.smoothDeltaTime * chunkCount;

					InvokePreMods (GetUseableModifiers ());

					// Loop through every chunk.
					for (var currentChunkIndex = 0; currentChunkIndex < chunkCount; currentChunkIndex++)
					{
						if (refreshModifiersEveryFrame)
							RefreshModifiers ();

						OnAutoChunkStart.Invoke ();

						ModifyChunk (chunkSize * currentChunkIndex, chunkSize * (currentChunkIndex + 1) + ((currentChunkIndex + 1 == chunkCount) ? CalcVertRemainder (GetVertCount (), chunkSize, chunkCount) : 0), GetUseableModifiers ());

						OnAutoChunkFinish.Invoke ();

						// The current chunk's modifications are finished so the next chunk will start being processed.
						CurrentModifiedChunkIndex++;

						// If there's more than one chunk, wait a frame.
						if (chunkCount > 1)
							yield return null;
					}

					// Update the mesh to reflect the new modifications.
					ApplyModifications ();

					InvokePostMods (GetUseableModifiers ());

					OnAutoUpdateFinish.Invoke ();
				}
				yield return null;
			}
		}

		/// <summary>
		/// Finds the number of vertices that can't be grouped in the chunks.
		/// </summary>
		private static int CalcVertRemainder (int vertCount, int chunkSize, int chunkAmount)
		{
			return vertCount - (chunkSize * chunkAmount);
		}
	}
}
