﻿using RTSLockstep;
using System;
using System.Collections.Generic;
using UnityEngine;

public class WallPositioningHelper : MonoBehaviour
{
    public GameObject pillarPrefab;
    public GameObject wallPrefab;
    // distance between poles to trigger spawning next segement
    public int poleOffset;  // = 10
    private const int pillarRangeOffset = 3;

    private Vector3 _currentPos;
    private bool _isPlacingWall;

    private GameObject startPillar;
    private GameObject lastPillar;
    private Vector3 endPillarPos;

    private List<GameObject> _pillarPrefabs;
    private Dictionary<int, GameObject> _wallPrefabs;
    public Transform OrganizerWallSegments { get; private set; }
    private bool _startSnapped;
    private bool _endSnapped;

    //   private List<GameObject> wallSegments;
    private int lastWallLength;

    public void Setup()
    {
        OrganizerWallSegments = LSUtility.CreateEmpty().transform;
        OrganizerWallSegments.gameObject.name = "OrganizerWallSegments";

        _startSnapped = false;
        _endSnapped = false;
        _isPlacingWall = false;
        _pillarPrefabs = new List<GameObject>();
        _wallPrefabs = new Dictionary<int, GameObject>();
    }

    public void Visualize(Vector3 pos)
    {
        if (!_isPlacingWall)
        {
            GameObject closestPillar = ClosestPillarTo(pos, pillarRangeOffset);
            if (closestPillar.IsNotNull())
            {
                ConstructionHandler.GetTempStructure().transform.position = closestPillar.transform.position;
                ConstructionHandler.GetTempStructure().transform.rotation = closestPillar.transform.rotation;
                _startSnapped = true;
            }
            else
            {
                _startSnapped = false;
                ConstructionHandler.GetTempStructure().transform.position = Positioning.GetSnappedPosition(pos);
            }
        }
        else
        {
            ConstructionHandler.GetTempStructure().transform.position = Positioning.GetSnappedPosition(pos);
        }
    }

    public void OnRightClick()
    {
        if (_isPlacingWall)
        {
            ClearTemporaryWalls();
        }
    }

    public void OnLeftClickUp()
    {
        if (_isPlacingWall)
        {
            SetWall();
        }
    }

    public void OnLeftClickDrag()
    {
        if (_isPlacingWall)
        {
            UpdateWall();
        }
        else
        {
            CreateStartPillar();
        }
    }

    public bool IsPlacingWall()
    {
        return this._isPlacingWall;
    }

    private void CreateStartPillar()
    {
        Vector3 startPos = ConstructionHandler.GetTempStructure().transform.position;

        // create initial pole
        if (!_startSnapped)
        {
            startPillar = Instantiate(pillarPrefab, startPos, Quaternion.identity) as GameObject;
            _pillarPrefabs.Add(startPillar);
            startPillar.transform.parent = OrganizerWallSegments;
        }
        else
        {
            startPillar = ClosestPillarTo(startPos, pillarRangeOffset);
            _pillarPrefabs.Add(startPillar);
        }

        lastPillar = startPillar;
        lastWallLength = 0;
        _isPlacingWall = true;
    }

    public GameObject ClosestPillarTo(Vector3 worldPoint, float distance)
    {
        GameObject closest = null;
        float currentDistance = Mathf.Infinity;
        string searchTag = "WallPillar";
        foreach (Transform child in OrganizerWallSegments)
        {
            if (child.gameObject.tag == searchTag)
            {
                currentDistance = Vector3.Distance(worldPoint, child.gameObject.transform.position);
                if (currentDistance < distance)
                {
                    closest = child.gameObject;
                }
            }
        }
        return closest;
    }

    private void SetWall()
    {
        //foreach (GameObject ws in wallSegments)
        //{
        //    ConstructionHandler.SetBuildQueue(ws);
        //}

        //wallSegments.Clear();

        //check if last pole was ever set
        if (!_endSnapped
            && _pillarPrefabs.Count == _wallPrefabs.Count)
        {
            Vector3 endPos = ConstructionHandler.GetTempStructure().transform.position;
            CreateWallPillar(endPos);
        }

        _isPlacingWall = false;
        _startSnapped = false;

        _pillarPrefabs.Clear();
        _wallPrefabs.Clear();
    }

    private void UpdateWall()
    {
        _currentPos = RTSInterfacing.GetWorldPos3(Input.mousePosition);

        if (_pillarPrefabs.Count > 0)
        {
            lastPillar = _pillarPrefabs[_pillarPrefabs.Count - 1];
        }

        if (lastPillar.IsNotNull())
        {
            if (!_currentPos.Equals(lastPillar.transform.position))
            {
                GameObject closestPillar = ClosestPillarTo(_currentPos, pillarRangeOffset);

                if (closestPillar.IsNotNull())
                {
                    if (closestPillar.transform.position != lastPillar.transform.position)
                    {
                        ConstructionHandler.GetTempStructure().transform.position = closestPillar.transform.position;
                        ConstructionHandler.GetTempStructure().transform.rotation = closestPillar.transform.rotation;
                        _endSnapped = true;
                    }
                }
                else
                {
                    _endSnapped = false;
                }

                endPillarPos = ConstructionHandler.GetTempStructure().transform.position;

                int wallLength = (int)Math.Round(Vector3.Distance(startPillar.transform.position, endPillarPos));
                int lastToPosDistance = (int)Math.Round(Vector3.Distance(_currentPos, lastPillar.transform.position));
                int endToLastDistance = (int)Math.Round(Vector3.Distance(endPillarPos, lastPillar.transform.position));
                // ensure end pole is far enough from start pole
                if (wallLength > lastWallLength)
                {
                    // ensure last instantiated pole is far enough from current pos
                    // and end pole is far enough from last pole
                    if (endToLastDistance >= poleOffset)
                    {
                        CreateWallPillar(_currentPos);
                    }
                    else if (lastToPosDistance >= 1)
                    {
                        CreateWallSegment(_currentPos);
                    }
                }
                else if (wallLength < lastWallLength)
                {
                    if (lastToPosDistance <= 1)
                    {
                        RemoveLastWallSegment();
                    }
                }

                lastWallLength = wallLength;
            }
        }

        AdjustWallSegments();
    }

    private void CreateWallPillar(Vector3 _currentPos)
    {
        GameObject newPillar = Instantiate(pillarPrefab, _currentPos, Quaternion.identity);
        newPillar.transform.LookAt(lastPillar.transform);
        _pillarPrefabs.Add(newPillar);
        newPillar.transform.parent = OrganizerWallSegments;
    }

    private void CreateWallSegment(Vector3 _currentPos)
    {
        int ndx = _pillarPrefabs.IndexOf(lastPillar);
        //only create wall segment if dictionary doesn't contain pillar index
        if (!_wallPrefabs.ContainsKey(ndx))
        {
            Vector3 middle = 0.5f * (endPillarPos + lastPillar.transform.position);

            GameObject newWall = Instantiate(wallPrefab, middle, Quaternion.identity);
            newWall.SetActive(true);
            _wallPrefabs.Add(ndx, newWall);
            newWall.transform.parent = OrganizerWallSegments;
        }
    }

    private void RemoveLastWallSegment()
    {
        if (_pillarPrefabs.Count > 0)
        {
            int ndx = _pillarPrefabs.Count - 1;
            Destroy(_pillarPrefabs[ndx].gameObject);
            _pillarPrefabs.RemoveAt(ndx);
            if (_wallPrefabs.Count > 0)
            {
                GameObject wallSegement;
                if (_wallPrefabs.TryGetValue(ndx, out wallSegement))
                {
                    Destroy(wallSegement.gameObject);
                    _wallPrefabs.Remove(ndx);
                }
            }
        }

        if (_pillarPrefabs.Count == 0)
        {
            lastPillar = startPillar;
        }
    }

    private void AdjustWallSegments()
    {
        startPillar.transform.LookAt(endPillarPos);
        ConstructionHandler.GetTempStructure().transform.LookAt(startPillar.transform.position);

        if (_pillarPrefabs.Count > 0)
        {
            GameObject adjustBasePole = startPillar;
            for (int i = 0; i < _pillarPrefabs.Count; i++)
            {
                // no need to adjust start pillar
                if (i > 0)
                {
                    Vector3 newPos = adjustBasePole.transform.position + startPillar.transform.TransformDirection(new Vector3(0, 0, poleOffset));
                    _pillarPrefabs[i].transform.position = newPos;
                    _pillarPrefabs[i].transform.rotation = startPillar.transform.rotation;
                }


                if (_wallPrefabs.Count > 0)
                {
                    int ndx = _pillarPrefabs.IndexOf(_pillarPrefabs[i]);
                    GameObject wallSegement;
                    if (_wallPrefabs.TryGetValue(ndx, out wallSegement))
                    {
                        GameObject nextPillar;
                        if (i + 1 < _pillarPrefabs.Count)
                        {
                            nextPillar = _pillarPrefabs[i + 1];
                        }
                        else
                        {
                            nextPillar = ConstructionHandler.GetTempStructure();
                        }

                        float distance = Vector3.Distance(adjustBasePole.transform.position, nextPillar.transform.position);
                        wallSegement.transform.localScale = new Vector3(wallSegement.transform.localScale.x, wallSegement.transform.localScale.y, distance);
                        wallSegement.transform.rotation = adjustBasePole.transform.rotation;

                        Vector3 middle = 0.5f * (nextPillar.transform.position + adjustBasePole.transform.position);
                        wallSegement.transform.position = middle;
                    }
                }

                adjustBasePole = _pillarPrefabs[i];
            }
        }
    }

    private void ClearTemporaryWalls()
    {
        _isPlacingWall = false;
        _pillarPrefabs.Clear();
        _wallPrefabs.Clear();
    }

    private void OnDestroy()
    {
        if (OrganizerWallSegments.IsNotNull()
            && OrganizerWallSegments.childCount > 0)
        {
            Destroy(OrganizerWallSegments.gameObject);
        }
    }
}
